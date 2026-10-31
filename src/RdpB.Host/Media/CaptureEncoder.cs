using FFmpeg.AutoGen;
using RdpB.Core.Media;

namespace RdpB.Host.Media;

public sealed record EncodedPacket(byte[] Data, bool IsKeyframe, long Pts);

/// <summary>
/// 計畫 §3:在程序內用 libavfilter 跑 ddagrab 擷取 D3D11 texture,直接交給硬體編碼器
/// (零拷貝)。編碼器依序試 h264_nvenc → h264_amf → h264_qsv → libx264,第一個 avcodec_open2
/// 成功的就用。不開 ffmpeg 子行程,這樣才能隨時呼叫 <see cref="RequestKeyframe"/> 強制出 IDR。
///
/// **已驗證**:在這台機器(RTX 4070)上,h264_nvenc 這條路徑真的跑過——ddagrab 擷取到
/// 2560x1600 的真實桌面畫面,編碼器零拷貝吃 D3D11 hw frame,連續編出多張 100~300KB 的封包。
/// **已驗證(2026-10-31)**:nvenc 被其他程式(例如 Discord 的「剪輯」背景錄製)佔滿工作階段
/// 名額、`avcodec_open2` 開不起來時,真的會退到 libx264 這條路——退下去之後發現 hwdownload
/// 那段一開始完全沒做對(見 <see cref="EncodeOneFrame"/> 裡的詳細說明:目的地 frame 沒給
/// 寬高,而且 ddagrab 吐出來的其實是 BGRA 不是 YUV),修好後這條路才真的能編出東西。
/// **尚未驗證**:h264_amf/h264_qsv 這兩條走的是同一套硬體路徑邏輯,但這台機器沒有 AMD/Intel
/// GPU,沒辦法實測。
/// </summary>
public sealed unsafe class CaptureEncoder : IDisposable
{
    private static readonly string[] EncoderCandidates = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    private AVFilterGraph* _graph;
    private AVFilterContext* _sinkCtx;
    private AVCodecContext* _encCtx;
    private AVBufferRef* _hwDeviceRef; // libx264 fallback 轉換用
    private SwsContext* _downloadSwsCtx; // libx264 fallback:BGRA(下載下來的) -> NV12(編碼器要的)
    private AVFrame* _nv12Frame; // libx264 fallback:跟 _downloadSwsCtx 同樣道理,重複用同一個
                                  // frame+緩衝區,不要每一幀都 alloc/free 一次(code review 抓到的坑)
    private int _scaledWidth, _scaledHeight; // Kyra 要求的畫質縮放——只套用在 libx264 軟體路徑,
                                              // 見 Start()/EncodeOneFrame 上的說明

    private Thread? _loop;
    private volatile bool _running;
    private volatile bool _forceKeyframe;
    private long _ptsCounter;

    public string? SelectedEncoder { get; private set; }
    public event Action<EncodedPacket>? PacketEncoded;
    public event Action<Exception>? Faulted;

    /// <summary><paramref name="fps"/> 只能是 <see cref="FrameRateOptions.AllowedValues"/>
    /// 列的那幾個值(30/45/60/90/120/144),跟 GUI 的下拉選單一致——不是隨便給一個數字就收。
    ///
    /// <paramref name="scalePercent"/>(1~100,預設 100 = 不縮放)是 Kyra 要求的畫質縮放,
    /// **只對 libx264 軟體編碼路徑生效**——硬體編碼器(nvenc/amf/qsv)維持零拷貝直接吃
    /// ddagrab 的原生解析度 D3D11 hw frame,縮放需要額外的 D3D11 層級處理(video processor
    /// 或專用的 scale_d3d11 濾鏡),跟計畫文件裡「不經過 CPU 直接 present」那條優化路線
    /// 一樣是刻意延後的範圍,不是忘記做。縮放最有感的正是軟體編碼這個情境(CPU 編碼解析度
    /// 越高越吃重),優先做這個已經能解決實際問題。</summary>
    public void Start(int monitorIndex, int fps, int bitrateKbps, string? forceEncoder = null, int scalePercent = 100)
    {
        FrameRateOptions.Validate(fps);
        if (scalePercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(scalePercent), scalePercent, "scalePercent 只能是 1~100");
        FfmpegRuntime.EnsureInitialized();

        _graph = ffmpeg.avfilter_graph_alloc();
        var ddagrab = ffmpeg.avfilter_get_by_name("ddagrab");
        if (ddagrab == null) throw new InvalidOperationException("這份 FFmpeg 沒有 ddagrab 濾鏡(需要 full build)");

        AVFilterContext* srcCtx = null;
        int ret = ffmpeg.avfilter_graph_create_filter(&srcCtx, ddagrab, "src",
            $"output_idx={monitorIndex}:framerate={fps}", null, _graph);
        ThrowIfError(ret, "建立 ddagrab 濾鏡失敗");

        var sinkFilter = ffmpeg.avfilter_get_by_name("buffersink");
        AVFilterContext* sinkCtx = null;
        ret = ffmpeg.avfilter_graph_create_filter(&sinkCtx, sinkFilter, "sink", null, null, _graph);
        ThrowIfError(ret, "建立 buffersink 失敗");
        _sinkCtx = sinkCtx;

        ret = ffmpeg.avfilter_link(srcCtx, 0, sinkCtx, 0);
        ThrowIfError(ret, "連接濾鏡失敗");

        ret = ffmpeg.avfilter_graph_config(_graph, null);
        ThrowIfError(ret, "設定 filtergraph 失敗");

        // 提前設 true(本來是暖機拉完第一幀之後才設)——PullFrameBlocking 接下來要靠這個旗標
        // 才能被 Dispose() 即時中斷,設太晚等於暖機那次呼叫完全不受這個保護,見
        // PullFrameBlocking 上的註解。
        _running = true;

        // 先拉一張幀才知道解析度和 hw_frames_ctx,拿去開編碼器。
        var firstFrame = ffmpeg.av_frame_alloc();
        try
        {
            PullFrameBlocking(firstFrame);
            OpenEncoder(firstFrame, bitrateKbps, fps, forceEncoder, scalePercent);
            EncodeOneFrame(firstFrame); // 第一幀餵進去,暖機(見探測結果,前 2~3 幀通常沒輸出)
        }
        finally
        {
            ffmpeg.av_frame_unref(firstFrame);
            ffmpeg.av_frame_free(&firstFrame);
        }

        _loop = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "RdpB-CaptureEncode" };
        _loop.Start();
    }

    /// <summary>強制下一幀編成 IDR——計畫 §1「TCP 丟幀之後才救得回來」要靠這個。</summary>
    public void RequestKeyframe() => _forceKeyframe = true;

    private void Loop()
    {
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            while (_running)
            {
                try
                {
                    PullFrameBlocking(frame);
                    EncodeOneFrame(frame);
                }
                finally
                {
                    ffmpeg.av_frame_unref(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止——PullFrameBlocking 看到 _running 變 false,主動放棄等待拋出來的,
            // 不是真的故障,不用通知 Faulted(下面那個 catch 的 `when (_running)` 在這個
            // 時間點一定是 false,接不住這個例外,得單獨擋,不然就是背景執行緒上一個沒人接的
            // 例外,直接砸死整個程式——真的在這台機器上炸出來的坑,見 PullFrameBlocking 上
            // 的註解)。
        }
        catch (Exception ex) when (_running)
        {
            Faulted?.Invoke(ex);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>擋到抓到一幀為止(或出錯,或 <see cref="_running"/> 被外部設成 false)。
    /// Start() 裡拉第一幀跟 Loop() 裡拉後續幀都用這個。
    ///
    /// 真的在這台機器上炸出來的坑(重連之後第二次連線整個連不上畫面才抓到):這裡原本完全
    /// 不看 `_running`,只在每次 `EAGAIN` 重試的空檔睡 5ms——理論上沒問題,因為 Loop()
    /// 迴圈最外層會在下一次 `PullFrameBlocking` 返回後才檢查 `_running` 決定要不要繼續。
    /// 但「下一次返回」這個前提,建立在 ddagrab 一定會在合理時間內吐出新畫面這個假設上——
    /// 一旦這個假設不成立(畫面長時間沒變化、Desktop Duplication session 因為某些原因卡住又
    /// 沒有明確回報錯誤,只是一直回 EAGAIN),這個方法就會無限期卡在這個迴圈裡,`Dispose()`
    /// 的 `_loop.Join()` 跟著永遠等不到底(就算逾時了也只能選擇洩漏原生資源、不敢釋放)。
    /// 下一次連線要重新 `avfilter_graph_create_filter` 開一個新的 ddagrab 時,舊的那個
    /// Desktop Duplication session 還被卡住的執行緒佔著,新的開不起來,整個串流直接啟動
    /// 失敗——表現成「重連之後 Host 編碼器永遠量測中、畫面永遠黑」。
    /// 改成每次 EAGAIN 重試都順便看一眼 `_running`,false 就直接不堵塞拉幀、拋一個清楚的
    /// 例外提早結束——Loop() 原本的 catch 已經用 `when (_running)` 擋掉這種「本來就要停」
    /// 的情況,不會被誤判成真的故障。</summary>
    private void PullFrameBlocking(AVFrame* frame)
    {
        while (true)
        {
            int ret = ffmpeg.av_buffersink_get_frame(_sinkCtx, frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                if (!_running) throw new OperationCanceledException("CaptureEncoder 正在停止,放棄等待下一幀");
                Thread.Sleep(5);
                continue;
            }
            ThrowIfError(ret, "擷取畫面失敗");
            return;
        }
    }

    private void OpenEncoder(AVFrame* sampleFrame, int bitrateKbps, int fps, string? forceEncoder, int scalePercent)
    {
        var names = forceEncoder is null ? EncoderCandidates : [forceEncoder];
        Exception? lastError = null;

        // H.264 4:2:0 的色度平面要整數,寬高無條件捨去到偶數,比無條件進位安全(不會超出
        // 原始擷取範圍)。scalePercent=100 時這組數字就等於原生解析度(本來就是偶數,這個
        // 計算是 no-op),兩條路徑(硬體/軟體)共用同一份,EncodeOneFrame 也會用到。
        // code review 抓到的坑:這兩行以前放在下面的 foreach 裡,每試一個候選編碼器就重算
        // 一次——sampleFrame/scalePercent 在整個迴圈裡根本不會變,搬到迴圈外面一次算好。
        _scaledWidth = Math.Max(2, sampleFrame->width * scalePercent / 100 / 2 * 2);
        _scaledHeight = Math.Max(2, sampleFrame->height * scalePercent / 100 / 2 * 2);

        foreach (var name in names)
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null) { lastError = new InvalidOperationException($"{name}:找不到這個編碼器"); continue; }

            bool isHardware = name is "h264_nvenc" or "h264_amf" or "h264_qsv";

            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (isHardware)
            {
                // 縮放只對軟體編碼路徑生效(見 Start() 上的說明),硬體編碼器維持原生解析度。
                ctx->width = sampleFrame->width;
                ctx->height = sampleFrame->height;
            }
            else
            {
                ctx->width = _scaledWidth;
                ctx->height = _scaledHeight;
            }
            ctx->time_base = new AVRational { num = 1, den = 90_000 };
            // 真的在這台機器上炸出來的坑:只設 time_base 不夠——libx264 算巨集區塊速率
            // (決定要用哪個 H.264 level)用的是 framerate,不是 time_base。沒設的話預設是
            // 0/1,libx264 會從別處(推測是誤用了 time_base 的分母)湊出一個離譜的數字,
            // 日誌裡看到「MB rate (1440000000) > level limit」——1440000000 剛好等於
            // 每幀巨集區塊數乘以 90000(time_base 的分母),不是真正的 30fps。算出來的
            // level 不合規,編出來的 bitstream Client 解不了,甚至讓解碼器直接原生當掉。
            ctx->framerate = new AVRational { num = fps, den = 1 };
            ctx->bit_rate = bitrateKbps * 1000L;
            ctx->gop_size = int.MaxValue; // 無限 GOP,需要時才插 IDR(§3)
            ctx->max_b_frames = 0;        // 低延遲:不用 B-frame

            if (isHardware)
            {
                ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(sampleFrame->hw_frames_ctx);
                if (name == "h264_nvenc")
                {
                    ffmpeg.av_opt_set(ctx->priv_data, "preset", "p1", 0);
                    ffmpeg.av_opt_set(ctx->priv_data, "tune", "ll", 0);
                    ffmpeg.av_opt_set(ctx->priv_data, "rc", "cbr", 0);
                    // NVENC 自己管 GOP,預設不理會 frame->flags 的 AV_FRAME_FLAG_KEY 請求——
                    // 沒開這個選項,RequestKeyframe() 會變成完全沒效果(實測發現的真坑)。
                    ffmpeg.av_opt_set(ctx->priv_data, "forced-idr", "1", 0);
                }
            }
            else
            {
                // libx264 軟體路徑:ddagrab 給的是 D3D11 hw frame,要先 hwdownload 成系統記憶體。
                ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
                ffmpeg.av_opt_set(ctx->priv_data, "preset", "ultrafast", 0);
                ffmpeg.av_opt_set(ctx->priv_data, "tune", "zerolatency", 0);
            }

            int ret = ffmpeg.avcodec_open2(ctx, codec, null);
            if (ret < 0)
            {
                lastError = new InvalidOperationException($"{name}:avcodec_open2 失敗({DescribeError(ret)})");
                ffmpeg.avcodec_free_context(&ctx);
                continue;
            }

            _encCtx = ctx;
            SelectedEncoder = name;
            return;
        }

        throw new InvalidOperationException("所有編碼器都開不起來,最後一個錯誤:" + lastError?.Message, lastError);
    }

    private void EncodeOneFrame(AVFrame* frame)
    {
        AVFrame* toSend = frame;
        AVFrame* downloaded = null;

        try
        {
            bool isHardwareEncoder = _encCtx->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11;
            if (!isHardwareEncoder)
            {
                // 真的在這台機器上炸出來的坑(兩層):
                // 1. av_hwframe_transfer_data 的目的地 frame 沒給 width/height,底層不知道要
                //    配置多大的緩衝區,直接回傳 EINVAL——這個先補上了,但補完還是同一個錯誤。
                // 2. 真正的原因:ddagrab 吐出來的 D3D11 texture 其實是 RGB 家族(實測用
                //    ffmpeg 直接跑 ddagrab 濾鏡,log 印出來是「d3d11(pc, gbr/bt709/...)」,
                //    對應 DXGI 的 B8G8R8A8,也就是 BGRA),不是 YUV/NV12——Desktop Duplication
                //    API 本來就是這樣,跟螢幕擷取的色彩格式一致。hwframe_transfer_data 只搬
                //    記憶體、不做色彩空間轉換,目的地 format 給 NV12 等於要求它把 RGB 直接
                //    硬套成 YUV 的記憶體佈局,當然也是 EINVAL。正確流程是先下載成 BGRA(跟
                //    來源一致),再用 sws_scale 轉成 libx264 要的 NV12。
                downloaded = ffmpeg.av_frame_alloc();
                downloaded->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                downloaded->width = frame->width;
                downloaded->height = frame->height;
                int dret = ffmpeg.av_hwframe_transfer_data(downloaded, frame, 0);
                ThrowIfError(dret, "hwframe transfer 失敗(libx264 軟體路徑)");

                // 這個 frame 整個物件的生命週期是跟著 CaptureEncoder 本身,不是這次呼叫——
                // 不用每幀 alloc/free 一次(code review 複查抓到的坑:之前每幀都配置一個新的
                // AVFrame + 緩衝區,熱路徑裡白白多一輪 malloc/free),Dispose() 裡統一釋放。
                // 也連帶修掉另一個 code review 抓到的坑:原本如果 av_frame_get_buffer 失敗,
                // 剛配置好的 nv12 frame 會在例外往外飄的路上漏掉沒釋放(finally 只認得
                // downloaded,不認得這個區域變數)——改成欄位之後,不管這次呼叫成不成功,
                // 下次呼叫或 Dispose() 都看得到同一個指標,不會漏。
                // 寬高用 _scaledWidth/_scaledHeight(Kyra 要求的畫質縮放,見 Start() 上的
                // 說明),不是 downloaded 的原生寬高——sws_scale 本來就是「縮放+轉色彩格式」
                // 一次做完,不用額外再多一道縮放步驟。scalePercent=100 時兩者數值相同。
                if (_nv12Frame == null) _nv12Frame = ffmpeg.av_frame_alloc();
                ffmpeg.av_frame_unref(_nv12Frame);
                _nv12Frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                _nv12Frame->width = _scaledWidth;
                _nv12Frame->height = _scaledHeight;
                int aret = ffmpeg.av_frame_get_buffer(_nv12Frame, 0);
                ThrowIfError(aret, "配置 NV12 緩衝區失敗(libx264 軟體路徑)");

                _downloadSwsCtx = ffmpeg.sws_getCachedContext(_downloadSwsCtx,
                    downloaded->width, downloaded->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                    _nv12Frame->width, _nv12Frame->height, AVPixelFormat.AV_PIX_FMT_NV12,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                ffmpeg.sws_scale(_downloadSwsCtx, downloaded->data, downloaded->linesize, 0,
                    downloaded->height, _nv12Frame->data, _nv12Frame->linesize);

                toSend = _nv12Frame;
            }

            toSend->pts = _ptsCounter++;
            if (_forceKeyframe)
            {
                toSend->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
                toSend->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                _forceKeyframe = false;
            }
            else
            {
                toSend->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE; // 讓編碼器自己決定
            }

            int ret = ffmpeg.avcodec_send_frame(_encCtx, toSend);
            if (ret < 0) throw new InvalidOperationException($"send_frame 失敗:{DescribeError(ret)}");

            var pkt = ffmpeg.av_packet_alloc();
            try
            {
                while (true)
                {
                    ret = ffmpeg.avcodec_receive_packet(_encCtx, pkt);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    ThrowIfError(ret, "receive_packet 失敗");

                    var data = new byte[pkt->size];
                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)pkt->data, data, 0, pkt->size);
                    bool isKey = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                    PacketEncoded?.Invoke(new EncodedPacket(data, isKey, pkt->pts));
                    ffmpeg.av_packet_unref(pkt);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&pkt);
            }
        }
        finally
        {
            if (downloaded != null)
            {
                ffmpeg.av_frame_unref(downloaded);
                ffmpeg.av_frame_free(&downloaded);
            }
        }
    }

    private static void ThrowIfError(int ret, string message)
    {
        if (ret < 0) throw new InvalidOperationException($"{message}:{DescribeError(ret)}");
    }

    private static string DescribeError(int err)
    {
        var buf = new byte[1024];
        fixed (byte* p = buf) { ffmpeg.av_strerror(err, p, 1024); }
        return System.Text.Encoding.ASCII.GetString(buf).TrimEnd('\0');
    }

    public void Dispose()
    {
        _running = false;
        // 真的在這台機器上炸出來的坑:原本不管 Join 有沒有真的等到執行緒結束,逾時(2 秒)
        // 一到就直接往下釋放 _encCtx/_graph 等原生資源——如果 Loop() 那條執行緒當下剛好還
        // 卡在 EncodeOneFrame() 裡(libx264 fallback 路徑這次多了 hwdownload+sws_scale,
        // 比單純硬體編碼慢得多,2 秒不一定夠),就會變成一邊釋放一邊還有別的執行緒在用同一塊
        // 記憶體——不是偶發的測試環境問題,是真的會讓整個行程直接當掉(退出串流視窗之後
        // RdpB.exe 整個不見,下一次連線當然連不上,因為根本沒有 Host 在跑)。
        // 拉長逾時(原本 2 秒 → 10 秒,libx264 fallback 單幀變慢後 2 秒太緊),但還是設上限,
        // 不要真的完全不設逾時——如果 Loop() 真的卡死(例如 ddagrab 的 Desktop Duplication
        // session 失效又沒有正常丟例外),Dispose() 永遠等不到底,會連帶卡住整個 StopStreaming/
        // 應用程式關閉流程。逾時了就不要再去動那些原生資源(寧可這次洩漏記憶體,應用程式反正
        // 馬上要關;也不要跟還在跑的執行緒搶同一塊記憶體,那才是真的會讓整個行程當掉的做法)。
        bool loopExited = _loop is null || _loop.Join(TimeSpan.FromSeconds(10));
        if (!loopExited)
        {
            RdpB.Core.Diagnostics.AppLog.WriteLine(
                "[CaptureEncoder] 編碼執行緒 10 秒內沒結束,放棄等待、不釋放原生資源(寧可洩漏,不要跟還在跑的執行緒搶記憶體)。");
            return;
        }

        if (_encCtx != null) { var e = _encCtx; ffmpeg.avcodec_free_context(&e); _encCtx = null; }
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; }
        if (_hwDeviceRef != null) { var h = _hwDeviceRef; ffmpeg.av_buffer_unref(&h); _hwDeviceRef = null; }
        if (_downloadSwsCtx != null) { ffmpeg.sws_freeContext(_downloadSwsCtx); _downloadSwsCtx = null; }
        if (_nv12Frame != null) { var n = _nv12Frame; ffmpeg.av_frame_free(&n); _nv12Frame = null; }
    }
}
