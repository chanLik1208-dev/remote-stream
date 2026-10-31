using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace RdpB.Client.Media;

public sealed record DecodedFrame(int Width, int Height, byte[] Bgra);

/// <summary>
/// 計畫 §4:D3D11VA 硬解——任何 GPU 都能用,不像 remote-stream 寫死 `*_cuvid`(只能 NV)。
/// 目前把解出來的 D3D11 texture 搬回系統記憶體轉成 BGRA(給簡單的視窗顯示用);
/// 計畫裡「不經過 CPU、D3D11VideoProcessor 轉色直接 present」那條零拷貝路線是之後的優化,
/// 這一版先求「畫面真的解得出來、顏色對不對」,見類別上的註解老實標出來。
/// </summary>
public sealed unsafe class Decoder : IDisposable
{
    private AVBufferRef* _hwDeviceCtx;
    private AVCodecContext* _decCtx;
    private SwsContext* _swsCtx;

    // 要保留這個委派的參考,不然原生那邊之後呼叫回來時,這個委派可能已經被 GC 回收掉。
    private static readonly AVCodecContext_get_format GetFormatDelegate = GetHwFormat;

    public bool UsingHardware { get; private set; }
    public string DecoderKind { get; private set; } = "(尚未開啟)";

    public event Action<DecodedFrame>? FrameDecoded;

    public void Open()
    {
        FfmpegRuntime.EnsureInitialized();

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null) throw new InvalidOperationException("找不到 h264 解碼器");

        _decCtx = ffmpeg.avcodec_alloc_context3(codec);

        AVBufferRef* hwDeviceCtx = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
        if (ret >= 0)
        {
            _hwDeviceCtx = hwDeviceCtx;
            _decCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _decCtx->get_format = GetFormatDelegate;
            UsingHardware = true;
            DecoderKind = "d3d11va";
        }
        else
        {
            UsingHardware = false;
            DecoderKind = $"sw(建立 D3D11VA device 失敗,退回軟解:{DescribeError(ret)})";
        }

        ret = ffmpeg.avcodec_open2(_decCtx, codec, null);
        if (ret < 0) throw new InvalidOperationException($"avcodec_open2(h264) 失敗:{DescribeError(ret)}");
    }

    private static AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* pixFmts)
    {
        for (var p = pixFmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == AVPixelFormat.AV_PIX_FMT_D3D11) return *p;
        return *pixFmts; // 對面沒給 D3D11 可選(不太會發生)就用第一個
    }

    /// <summary>餵一包 Annex-B H.264 資料進去,解出來的每一幀透過 <see cref="FrameDecoded"/> 送出。</summary>
    public void SubmitPacket(byte[] data)
    {
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            var buf = (byte*)ffmpeg.av_malloc((ulong)data.Length);
            Marshal.Copy(data, 0, (IntPtr)buf, data.Length);
            ffmpeg.av_packet_from_data(pkt, buf, data.Length); // pkt 接管 buf 的所有權

            int ret = ffmpeg.avcodec_send_packet(_decCtx, pkt);
            if (ret < 0) throw new InvalidOperationException($"send_packet 失敗:{DescribeError(ret)}");

            DrainFrames();
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
        }
    }

    private void DrainFrames()
    {
        var hwFrame = ffmpeg.av_frame_alloc();
        var cpuFrame = ffmpeg.av_frame_alloc();
        try
        {
            while (true)
            {
                int ret = ffmpeg.avcodec_receive_frame(_decCtx, hwFrame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) throw new InvalidOperationException($"receive_frame 失敗:{DescribeError(ret)}");

                EmitAsBgra(hwFrame, cpuFrame);
                ffmpeg.av_frame_unref(hwFrame);
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&hwFrame);
            ffmpeg.av_frame_free(&cpuFrame);
        }
    }

    private void EmitAsBgra(AVFrame* hwFrame, AVFrame* cpuFrame)
    {
        AVFrame* nv12Frame = hwFrame;
        if (UsingHardware)
        {
            ffmpeg.av_frame_unref(cpuFrame);
            // 跟 Host 端 CaptureEncoder 那個坑一模一樣:av_hwframe_transfer_data 的目的地
            // frame 不給 format/width/height,底層不知道要配置多大的緩衝區,直接回傳
            // Invalid argument——這條硬解路徑之前從沒真的收到過一幀(Host 那邊先掛了),
            // 所以一直沒踩到。D3D11VA 解碼吐出來的固定是 NV12,寬高照抄 hwFrame 的就好。
            cpuFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            cpuFrame->width = hwFrame->width;
            cpuFrame->height = hwFrame->height;
            int ret = ffmpeg.av_hwframe_transfer_data(cpuFrame, hwFrame, 0);
            if (ret < 0) throw new InvalidOperationException($"hwframe_transfer_data 失敗:{DescribeError(ret)}");
            nv12Frame = cpuFrame;
        }

        int w = nv12Frame->width, h = nv12Frame->height;
        var bgra = new byte[w * h * 4];

        _swsCtx = ffmpeg.sws_getCachedContext(_swsCtx, w, h, (AVPixelFormat)nv12Frame->format,
            w, h, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);

        fixed (byte* dst = bgra)
        {
            var dstData = new byte_ptrArray4 { [0] = dst };
            var dstLinesize = new int_array4 { [0] = w * 4 };
            ffmpeg.sws_scale(_swsCtx, nv12Frame->data, nv12Frame->linesize, 0, h, dstData, dstLinesize);
        }

        FrameDecoded?.Invoke(new DecodedFrame(w, h, bgra));

        if (UsingHardware) ffmpeg.av_frame_unref(cpuFrame);
    }

    private static string DescribeError(int err)
    {
        var buf = new byte[1024];
        fixed (byte* p = buf) { ffmpeg.av_strerror(err, p, 1024); }
        return System.Text.Encoding.ASCII.GetString(buf).TrimEnd('\0');
    }

    public void Dispose()
    {
        if (_swsCtx != null) { ffmpeg.sws_freeContext(_swsCtx); _swsCtx = null; }
        if (_decCtx != null) { var c = _decCtx; ffmpeg.avcodec_free_context(&c); _decCtx = null; }
        if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; }
    }
}
