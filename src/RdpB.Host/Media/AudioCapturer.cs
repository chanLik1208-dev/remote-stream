using Concentus.Enums;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using RdpB.Core.Media;

namespace RdpB.Host.Media;

/// <summary>
/// 計畫 §9:搬 remote-stream `AudioSender` 的 WASAPI loopback → Opus 編碼邏輯,改成
/// 透過事件把封好序號的 payload 交出去,呼叫端(App 的 Host 迴圈)自己送進 Session 的
/// Audio channel——這裡完全不碰網路。
/// 48kHz / 立體聲 / 20ms 一幀,跟 remote-stream 原本的參數一致(已經調過、聽感正常)。
/// </summary>
public sealed class AudioCapturer : IDisposable
{
    private const int Rate = 48000, Channels = 2, FrameSamples = 960; // 20ms

    private WasapiLoopbackCapture? _capture;
    private MediaFoundationResampler? _resampler;
    private Thread? _thread;
    private volatile bool _running;
    private ushort _seq;

    /// <summary>每編出一幀就觸發,payload 已經是 <see cref="AudioFramePacking.Pack"/> 過的格式,
    /// 呼叫端直接拿去 Session.SendAsync(Channel.Audio, MessageType.AudioFrame, payload)。</summary>
    public event Action<byte[]>? FrameEncoded;
    public event Action<Exception>? Faulted;

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "RdpB-AudioCapture" };
        _thread.Start();
    }

    private void Loop()
    {
        try { MediaFoundationApi.Startup(); } catch { /* 已經啟動過也沒關係 */ }

        try
        {
            _capture = new WasapiLoopbackCapture();
            var buffered = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };
            _capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0) buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            _capture.StartRecording();

            var outFormat = new WaveFormat(Rate, 16, Channels);
            _resampler = new MediaFoundationResampler(buffered, outFormat) { ResamplerQuality = 60 };

            // Concentus 建議改用 OpusCodecFactory 走原生函式庫(平台支援時效能較好),但那樣會多一個
            // 原生相依;純受管理的這個建構函式已經是 remote-stream 原本驗證過在用的路徑,先不換。
#pragma warning disable CS0618
            var encoder = new OpusEncoder(Rate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
#pragma warning restore CS0618
            encoder.Bitrate = 96_000;

            int frameBytes = FrameSamples * Channels * 2;
            var raw = new byte[frameBytes];
            var pcm = new short[FrameSamples * Channels];
            var opusOut = new byte[4000];

            while (_running)
            {
                int got = ReadFull(_resampler, raw, frameBytes);
                if (got < frameBytes) { Thread.Sleep(3); continue; }

                for (int i = 0; i < pcm.Length; i++)
                    pcm[i] = (short)(raw[i * 2] | (raw[i * 2 + 1] << 8));

                int len = encoder.Encode(pcm.AsSpan(0, FrameSamples * Channels), FrameSamples, opusOut, opusOut.Length);
                if (len > 0)
                {
                    var payload = AudioFramePacking.Pack(_seq++, opusOut.AsSpan(0, len));
                    FrameEncoded?.Invoke(payload);
                }
            }
        }
        catch (Exception ex) when (_running)
        {
            Faulted?.Invoke(ex);
        }
        finally
        {
            try { _capture?.StopRecording(); } catch { }
            try { _resampler?.Dispose(); } catch { }
            try { _capture?.Dispose(); } catch { }
        }
    }

    private static int ReadFull(IWaveProvider provider, byte[] buf, int count)
    {
        int offset = 0, emptyReads = 0;
        while (offset < count)
        {
            int n = provider.Read(buf, offset, count - offset);
            if (n <= 0)
            {
                if (++emptyReads > 1) break;
                Thread.Sleep(2);
                continue;
            }
            offset += n;
            emptyReads = 0;
        }
        return offset;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
