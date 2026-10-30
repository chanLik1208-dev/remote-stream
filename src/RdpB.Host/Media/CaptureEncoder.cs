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
/// **尚未驗證**:h264_amf/h264_qsv/libx264 這三條走的是同一套邏輯,但這台機器沒有 AMD/Intel
/// GPU,沒辦法實測;libx264 的軟體轉換路徑(hwdownload)也還沒跑過一次真的編碼。
/// </summary>
public sealed unsafe class CaptureEncoder : IDisposable
{
    private static readonly string[] EncoderCandidates = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    private AVFilterGraph* _graph;
    private AVFilterContext* _sinkCtx;
    private AVCodecContext* _encCtx;
    private AVBufferRef* _hwDeviceRef; // libx264 fallback 轉換用

    private Thread? _loop;
    private volatile bool _running;
    private volatile bool _forceKeyframe;
    private long _ptsCounter;

    public string? SelectedEncoder { get; private set; }
    public event Action<EncodedPacket>? PacketEncoded;
    public event Action<Exception>? Faulted;

    /// <summary><paramref name="fps"/> 只能是 <see cref="FrameRateOptions.AllowedValues"/>
    /// 列的那幾個值(30/45/60/90/120/144),跟 GUI 的下拉選單一致——不是隨便給一個數字就收。</summary>
    public void Start(int monitorIndex, int fps, int bitrateKbps, string? forceEncoder = null)
    {
        FrameRateOptions.Validate(fps);
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

        // 先拉一張幀才知道解析度和 hw_frames_ctx,拿去開編碼器。
        var firstFrame = ffmpeg.av_frame_alloc();
        try
        {
            PullFrameBlocking(firstFrame);
            OpenEncoder(firstFrame, bitrateKbps, forceEncoder);
            EncodeOneFrame(firstFrame); // 第一幀餵進去,暖機(見探測結果,前 2~3 幀通常沒輸出)
        }
        finally
        {
            ffmpeg.av_frame_unref(firstFrame);
            ffmpeg.av_frame_free(&firstFrame);
        }

        _running = true;
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
        catch (Exception ex) when (_running)
        {
            Faulted?.Invoke(ex);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>擋到抓到一幀為止(或出錯)。Start() 裡拉第一幀跟 Loop() 裡拉後續幀都用這個,
    /// 本身不看 `_running`——要不要停迴圈是呼叫端(Loop)的責任。</summary>
    private void PullFrameBlocking(AVFrame* frame)
    {
        while (true)
        {
            int ret = ffmpeg.av_buffersink_get_frame(_sinkCtx, frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Thread.Sleep(5); continue; }
            ThrowIfError(ret, "擷取畫面失敗");
            return;
        }
    }

    private void OpenEncoder(AVFrame* sampleFrame, int bitrateKbps, string? forceEncoder)
    {
        var names = forceEncoder is null ? EncoderCandidates : [forceEncoder];
        Exception? lastError = null;

        foreach (var name in names)
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null) { lastError = new InvalidOperationException($"{name}:找不到這個編碼器"); continue; }

            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            ctx->width = sampleFrame->width;
            ctx->height = sampleFrame->height;
            ctx->time_base = new AVRational { num = 1, den = 90_000 };
            ctx->bit_rate = bitrateKbps * 1000L;
            ctx->gop_size = int.MaxValue; // 無限 GOP,需要時才插 IDR(§3)
            ctx->max_b_frames = 0;        // 低延遲:不用 B-frame

            bool isHardware = name is "h264_nvenc" or "h264_amf" or "h264_qsv";
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
                // libx264 路徑:把 hw frame 搬回系統記憶體(NV12)。
                downloaded = ffmpeg.av_frame_alloc();
                downloaded->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                int dret = ffmpeg.av_hwframe_transfer_data(downloaded, frame, 0);
                ThrowIfError(dret, "hwframe transfer 失敗(libx264 軟體路徑)");
                toSend = downloaded;
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
        _loop?.Join(TimeSpan.FromSeconds(2));

        if (_encCtx != null) { var e = _encCtx; ffmpeg.avcodec_free_context(&e); _encCtx = null; }
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; }
        if (_hwDeviceRef != null) { var h = _hwDeviceRef; ffmpeg.av_buffer_unref(&h); _hwDeviceRef = null; }
    }
}
