using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RdpB.Core.Media;

namespace RdpB.Client.Media;

/// <summary>
/// 計畫 §9:搬 remote-stream `AudioReceiver` 的 Opus 解碼 → WASAPI 播放邏輯,加上
/// <see cref="AudioSequenceTracker"/> 做掉包偵測 + Opus PLC(傳 null 給 Decode 讓 Opus
/// 自己用前面的聲音特徵「猜」出遺失的那幾幀,比放靜音或直接跳過聽起來自然)。
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const int Rate = 48000, Channels = 2, FrameSamples = 960; // 20ms

    // 同樣理由(見 AudioCapturer):維持純受管理的建構函式,不換成 OpusCodecFactory 的原生路徑。
#pragma warning disable CS0618
    private readonly OpusDecoder _decoder = new(Rate, Channels);
#pragma warning restore CS0618
    private readonly AudioSequenceTracker _tracker = new();
    private readonly short[] _pcm = new short[FrameSamples * Channels];
    private readonly byte[] _pcmBytes = new byte[FrameSamples * Channels * 2];

    private BufferedWaveProvider? _buffer;
    private IWavePlayer? _output;

    public void Start()
    {
        var format = new WaveFormat(Rate, 16, Channels);
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };
        _output = new WasapiOut(AudioClientShareMode.Shared, 50);
        _output.Init(_buffer);
        _output.Play();
    }

    /// <summary>收到一包 Audio channel 的訊息就呼叫這個,payload 是
    /// <see cref="AudioFramePacking.Pack"/> 過的格式。</summary>
    public void SubmitFrame(ReadOnlySpan<byte> payload)
    {
        if (_buffer is null) throw new InvalidOperationException("還沒呼叫 Start()");
        if (!AudioFramePacking.TryUnpack(payload, out var seq, out var opusData)) return;

        var decision = _tracker.Observe(seq);
        switch (decision.Action)
        {
            case AudioFrameAction.Drop:
                return;

            case AudioFrameAction.ConcealThenPlay:
                for (int i = 0; i < decision.ConcealFrameCount; i++)
                    DecodeAndEnqueue(default, loss: true);
                DecodeAndEnqueue(opusData, loss: false);
                break;

            case AudioFrameAction.PlayNormally:
                DecodeAndEnqueue(opusData, loss: false);
                break;
        }
    }

    private void DecodeAndEnqueue(ReadOnlySpan<byte> opusData, bool loss)
    {
        // PLC:傳空 span 進去,讓 Opus 自己用前面的聲音特徵「猜」出遺失的這一幀。
        int samples = _decoder.Decode(loss ? default : opusData, _pcm.AsSpan(0, FrameSamples * Channels), FrameSamples, false);

        if (samples <= 0) return;

        int n = samples * Channels;
        for (int i = 0; i < n; i++)
        {
            _pcmBytes[i * 2] = (byte)(_pcm[i] & 0xff);
            _pcmBytes[i * 2 + 1] = (byte)((_pcm[i] >> 8) & 0xff);
        }
        _buffer!.AddSamples(_pcmBytes, 0, n * 2);
    }

    public void Dispose()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
    }
}
