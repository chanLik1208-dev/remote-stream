// 音訊:主機 WASAPI loopback → Opus → UDP;客戶端 收 → Opus 解碼 → WASAPI 播放
// 全程 48kHz / 立體聲 / 20ms 幀。主機端非 48k 會用 MediaFoundationResampler 轉。
using System.Net;
using System.Net.Sockets;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace RemotePlay;

// 主機:擷取系統輸出 → Opus 編碼 → 送到客戶端
sealed class AudioSender
{
    const int Rate = 48000, Ch = 2, Frame = 960; // 20ms
    readonly UdpClient _udp;
    volatile bool _run = true;
    IPEndPoint? _dest;
    Thread? _t;

    public AudioSender(UdpClient udp) { _udp = udp; }
    public void SetDest(IPEndPoint ep) { _dest = ep; }
    public void Start() { _t = new Thread(Loop) { IsBackground = true }; _t.Start(); }
    public void Stop() { _run = false; }

    void Loop()
    {
        WasapiLoopbackCapture? cap = null;
        MediaFoundationResampler? res = null;
        try
        {
            try { MediaFoundationApi.Startup(); } catch { }
            cap = new WasapiLoopbackCapture();
            var buffered = new BufferedWaveProvider(cap.WaveFormat)
            { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true, ReadFully = false };
            cap.DataAvailable += (_, e) => { if (e.BytesRecorded > 0) buffered.AddSamples(e.Buffer, 0, e.BytesRecorded); };
            cap.StartRecording();

            var outFmt = new WaveFormat(Rate, 16, Ch);
            res = new MediaFoundationResampler(buffered, outFmt) { ResamplerQuality = 60 };

            var enc = OpusEncoder.Create(Rate, Ch, OpusApplication.OPUS_APPLICATION_AUDIO);
            enc.Bitrate = 96000;

            int frameBytes = Frame * Ch * 2;
            var raw = new byte[frameBytes];
            var pcm = new short[Frame * Ch];
            var outBuf = new byte[4000];
            Console.WriteLine($"[host] 音訊擷取中（WASAPI loopback {cap.WaveFormat.SampleRate}Hz → 48k Opus）");

            while (_run)
            {
                if (_dest == null) { Thread.Sleep(20); continue; } // 等客戶端連上
                int got = ReadFull(res, raw, frameBytes);
                if (got < frameBytes) { Thread.Sleep(3); continue; }
                for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                int len = enc.Encode(pcm, 0, Frame, outBuf, 0, outBuf.Length);
                if (len > 0) { try { _udp.Send(outBuf, len, _dest); } catch { } }
            }
        }
        catch (Exception e) { Console.WriteLine($"[host] 音訊停止：{e.Message}"); }
        finally
        {
            try { cap?.StopRecording(); } catch { }
            try { res?.Dispose(); } catch { }
            try { cap?.Dispose(); } catch { }
        }
    }

    // 從 resampler 讀滿一幀（拉取模式，資料不足時短暫等待）
    static int ReadFull(IWaveProvider p, byte[] buf, int count)
    {
        int off = 0, empty = 0;
        while (off < count)
        {
            int n = p.Read(buf, off, count - off);
            if (n <= 0) { if (++empty > 1) break; Thread.Sleep(2); continue; }
            off += n; empty = 0;
        }
        return off;
    }
}

// 客戶端:收 Opus → 解碼 → WASAPI 播放
sealed class AudioReceiver
{
    const int Rate = 48000, Ch = 2, Frame = 960;
    readonly int _port;
    volatile bool _run = true;
    IWavePlayer? _out;
    Thread? _t;

    public AudioReceiver(int port) { _port = port; }
    public void Start() { _t = new Thread(Loop) { IsBackground = true }; _t.Start(); }
    public void Stop() { _run = false; try { _out?.Stop(); } catch { } }

    void Loop()
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(_port);
            var wf = new WaveFormat(Rate, 16, Ch);
            var buffered = new BufferedWaveProvider(wf) { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true };
            _out = new WasapiOut(AudioClientShareMode.Shared, 50);
            _out.Init(buffered);
            _out.Play();

            var dec = OpusDecoder.Create(Rate, Ch);
            var pcm = new short[Frame * Ch * 6];
            var bytes = new byte[pcm.Length * 2];
            var ep = new IPEndPoint(IPAddress.Any, 0);
            Console.WriteLine($"[client] 音訊播放就緒（UDP :{_port}）");

            while (_run)
            {
                byte[] data;
                try { data = udp.Receive(ref ep); }
                catch { if (_run) continue; else break; }

                int samples = dec.Decode(data, 0, data.Length, pcm, 0, Frame, false);
                if (samples > 0)
                {
                    int n = samples * Ch;
                    for (int i = 0; i < n; i++) { bytes[i * 2] = (byte)(pcm[i] & 0xff); bytes[i * 2 + 1] = (byte)((pcm[i] >> 8) & 0xff); }
                    buffered.AddSamples(bytes, 0, n * 2);
                }
            }
        }
        catch (Exception e) { Console.WriteLine($"[client] 音訊停止：{e.Message}"); }
        finally
        {
            try { _out?.Dispose(); } catch { }
            try { udp?.Close(); } catch { }
        }
    }
}
