using System.Net;
using RdpB.Core.Media;
using RdpB.Core.Pairing;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Core.Windows;
using RdpB.Host.Input;
using RdpB.Host.Media;
using CoreSession = RdpB.Core.Session.Session;

namespace RdpB.App.Services;

/// <summary>
/// 計畫 §8「本機當 Host」那個面板背後的邏輯,跟 Avalonia 完全分開方便測試。§1:Host 是
/// 監聽方,負責把 port 打開。這一版真的接上配對(§2)和串流(§3/§5/§9):
///
///  - 新連線先看 TLS 實際出示的憑證指紋在不在「已配對過的 Client」清單裡
///    (<see cref="RdpBConfig.TrustedClients"/>)——在,直接信任;不在,跑
///    <see cref="PairingCoordinator.WaitForPairingAsync"/> 要求對面提供正確 PIN。
///  - 配對過(不管是剛配對成功還是本來就信任)才會真的開始串流:啟動
///    <see cref="CaptureEncoder"/>/<see cref="AudioCapturer"/>,把畫面/音訊/輸入都接上
///    這個 Session。
///
/// **v1 範圍限制,老實標出來**:同時只服務一個 Client(跟 Client 端「第一階段只允許
/// 同時連一台」的設計一致)。已經有人在串流時,新連線配對完也不會搶過去,只會被拒絕
/// ——這是刻意的簡化,不是忘記做,多工作階段是之後的事。
/// </summary>
public sealed class HostListenerService : IAsyncDisposable
{
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(60);

    private readonly string _configPath;
    private readonly object _configLock = new(); // code review:避免兩條連線同時讀改存 config.json 互相蓋掉
    private TcpTlsTransportListener? _listener;
    private CancellationTokenSource? _acceptLoopCts;
    private Task? _acceptLoop;
    private System.Security.Cryptography.X509Certificates.X509Certificate2? _hostCertificate;

    /// <summary>0=空著,1=被佔用(不管是正在配對中,還是已經在串流)。code review 抓到的真坑:
    /// 原本只看 `_activeSession is not null` 擋第二個人,但那個欄位要等配對「成功」才會設,
    /// 兩個人在同一個 ~60 秒配對視窗內一起連進來,兩邊都通得過這個檢查,最後變成兩組
    /// CaptureEncoder/AudioCapturer 互搶同一個螢幕/音效裝置,輸的那組資源還洩漏。
    /// 一接受連線、還沒開始配對就先佔位,所有失敗出口都要記得釋放。</summary>
    private int _slotClaimed;

    private CoreSession? _activeSession;
    private CaptureEncoder? _capture;
    private AudioCapturer? _audio;
    private Injector? _injector;
    private GamepadSink? _gamepad;
    private int _connectedClientCount;

    public bool IsRunning { get; private set; }
    public string? Pin { get; private set; }
    public int Port { get; private set; }
    public int ConnectedClientCount => _connectedClientCount;
    public bool IsStreaming => _activeSession is not null;
    public string? SelectedEncoder => _capture?.SelectedEncoder;

    public event Action? StateChanged;

    /// <summary>配對成功了,但真的要開始串流(擷取/編碼)時失敗——診斷用,GUI 之後可以拿
    /// 這個在 Host 面板顯示白話文錯誤,而不是讓使用者乾等看不懂發生什麼事。</summary>
    public event Action<Exception>? StreamingStartFailed;

    public HostListenerService(string? configPath = null) => _configPath = configPath ?? ConfigStore.DefaultPath;

    public void Start(int port = DefaultPorts.Host)
    {
        if (IsRunning) return;

        var certPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB", "host-identity.bin");
        _hostCertificate = CertificateStore.LoadOrCreate(certPath, Environment.MachineName);

        _listener = TcpTlsTransportListener.Start(
            new IPEndPoint(IPAddress.Any, port), _hostCertificate, clientCertificateRequired: true);
        Port = _listener.BoundPort;
        Pin = PairingService.GeneratePin();
        Interlocked.Exchange(ref _connectedClientCount, 0);
        Interlocked.Exchange(ref _slotClaimed, 0);
        IsRunning = true;

        _acceptLoopCts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _hostCertificate, _acceptLoopCts.Token));

        StateChanged?.Invoke();
    }

    private async Task AcceptLoopAsync(
        TcpTlsTransportListener listener,
        System.Security.Cryptography.X509Certificates.X509Certificate2 hostCertificate,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpTlsTransport transport;
            try
            {
                transport = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; } // 單一連線失敗不拖累 accept loop(見計畫 §8 已修過的同一類 bug)

            Interlocked.Increment(ref _connectedClientCount);
            StateChanged?.Invoke();

            // 一接受連線就先佔位(不是等配對成功才佔)——這樣兩個人同時連進來,
            // 第二個會在這裡就被擋掉,不會兩邊都跑去配對、最後搶同一組硬體資源。
            if (Interlocked.CompareExchange(ref _slotClaimed, 1, 0) != 0)
            {
                try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
                continue;
            }

            _ = HandleNewConnectionAsync(transport, hostCertificate, ct);
        }
    }

    private async Task HandleNewConnectionAsync(
        TcpTlsTransport transport,
        System.Security.Cryptography.X509Certificates.X509Certificate2 hostCertificate,
        CancellationToken ct)
    {
        // 從這裡開始,任何一條退出路徑(除了「真的開始串流」那條)都要記得 ReleaseSlot()。
        try
        {
            if (transport.RemoteCertificate is null)
            {
                try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
                return;
            }

            var session = new CoreSession(transport);
            session.Start();

            var clientFingerprintHex = Convert.ToHexString(
                PairingService.ComputeFingerprint(transport.RemoteCertificate.RawData));

            bool alreadyTrusted = IsTrusted(clientFingerprintHex);

            if (!alreadyTrusted)
            {
                // timeout 直接交給 PairingCoordinator 自己處理,不用在這裡手動包一層
                // CancellationTokenSource——ct 仍然負責「Host 整個被叫停」這件事。
                var (result, _) = await PairingCoordinator.WaitForPairingAsync(
                    session, Pin ?? "", transport.RemoteCertificate, hostCertificate, ct, PairingTimeout).ConfigureAwait(false);

                if (result != PairingResult.Success)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                RecordTrustedClient(clientFingerprintHex, isNew: true);
            }
            else
            {
                RecordTrustedClient(clientFingerprintHex, isNew: false);
            }

            // code review 抓到的另一個真坑:配對(可能花到 60 秒)期間,使用者可能已經按了
            // 停止 Host——這裡要再檢查一次,不然 StopAsync() 以為乾淨停掉了,背地裡卻又冒出
            // 一個真的在跑的擷取器,變成使用者看不到、關不掉的殭屍串流。
            if (ct.IsCancellationRequested)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                await StartStreamingForAsync(session, ct).ConfigureAwait(false);
                return; // 成功:slot 留著佔用,交給 StopStreaming() 之後釋放,不要走到下面的 finally 釋放
            }
            catch (Exception ex)
            {
                // 已經在 StartStreamingForAsync 裡把 session 關掉、資源釋放乾淨了,這裡不用再做什麼,
                // 只是不要讓例外一路往外飄,砸了 accept loop(呼叫端是 fire-and-forget)。
                System.Diagnostics.Debug.WriteLine($"[HostListenerService] 串流啟動失敗:{ex}");
                StreamingStartFailed?.Invoke(ex);
                return;
            }
        }
        finally
        {
            // 走到這裡代表沒有成功進入串流狀態(上面成功路徑已經 return 跳過這個 finally 的
            // 釋放邏輯——不對,finally 一定會跑;用 _activeSession 是否已經設定來判斷才準確)。
            if (_activeSession is null)
                ReleaseSlot();
        }
    }

    private bool IsTrusted(string fingerprintHex)
    {
        lock (_configLock)
        {
            var config = ConfigStore.Load(_configPath);
            return config.TrustedClients.Any(c =>
                string.Equals(c.FingerprintHex, fingerprintHex, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>code review 抓到的真坑:兩條連線幾乎同時配對時,各自 Load → 改 → Save 整份
    /// config.json,後存的會蓋掉先存的,其中一邊的配對紀錄憑空消失。整個讀改存包進同一把鎖,
    /// 跟 GUI 那邊的 Save(SettingsViewModel 等)還是有可能撞在一起,但 Host 這裡至少自己
    /// 兩條連線之間不會互相蓋。</summary>
    private void RecordTrustedClient(string fingerprintHex, bool isNew)
    {
        lock (_configLock)
        {
            var config = ConfigStore.Load(_configPath);
            var existing = config.TrustedClients.FirstOrDefault(c =>
                string.Equals(c.FingerprintHex, fingerprintHex, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                config.TrustedClients.Add(new TrustedClientRecord
                {
                    FingerprintHex = fingerprintHex,
                    PairedAt = DateTimeOffset.UtcNow,
                    LastConnected = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.LastConnected = DateTimeOffset.UtcNow;
            }

            ConfigStore.Save(config, _configPath);
        }
    }

    private async Task StartStreamingForAsync(CoreSession session, CancellationToken ct)
    {
        var quality = ConfigStore.Load(_configPath).Quality;

        var injector = new Injector();
        var gamepad = new GamepadSink();
        var capture = new CaptureEncoder();
        var audio = new AudioCapturer();

        // CaptureEncoder/AudioCapturer 的事件是在它們自己的背景執行緒上直接呼叫的(不是透過
        // Task),session 關閉後再送一次是完全正常會發生的競態——SendAsync 這時會同步丟
        // ObjectDisposedException,沒包 try/catch 的話會直接砸死那條背景執行緒,整個行程
        // 跟著當掉(真的在整合測試裡炸過一次才抓到:Client 斷線那瞬間,Host 還在送下一幀)。
        capture.PacketEncoded += pkt => SafeSend(session, Channel.Video, MessageType.VideoFrame, pkt.Data);
        // IsKeyframe 不用另外送——H.264 bytestream 本身看得出哪張是關鍵幀,Decoder 不需要旁帶這個資訊。

        // code review 抓到的真坑(嚴重):Faulted 是 CaptureEncoder 自己的背景執行緒
        // (_loop)同步丟出來的。如果這裡直接呼叫 session.DisposeAsync(),會一路同步
        // 串到 StopStreaming() → capture.Dispose() → _loop.Join()——變成那條執行緒自己
        // join 自己,直接卡死到逾時,之後還在那條「理論上已經結束」的執行緒底下重入釋放
        // _encCtx 等資源。改成丟進 Task.Run,讓釋放動作在別的執行緒上做,_loop 能正常
        // 跑完 Loop() 自然結束,Join() 才真的等得到人。
        capture.Faulted += ex => _ = Task.Run(() => session.DisposeAsync().AsTask());

        audio.FrameEncoded += payload => SafeSend(session, Channel.Audio, MessageType.AudioFrame, payload);
        audio.Faulted += _ => { }; // 音訊掛了不該拖累整條串流,安靜忽略

        // 注意:IsStreaming/_activeSession 一定要等 capture.Start() 真的成功才設——不然
        // Start() 丟例外、或還沒跑完的這段空檔,IsStreaming 會騙人說已經在串流了(code review
        // 風格的坑,寫整合測試時自己先踩到:host.IsStreaming 量到 true,但 SelectedEncoder
        // 還是 null,兩個訊號不同步)。
        ct.ThrowIfCancellationRequested(); // 配對期間 Host 可能已經被叫停,見呼叫端的註解
        try
        {
            capture.Start(monitorIndex: 0, fps: quality.Fps, bitrateKbps: quality.BitrateKbps);
            audio.Start();
        }
        catch
        {
            capture.Dispose();
            audio.Dispose();
            await session.DisposeAsync().ConfigureAwait(false); // 配對成功但串不起來,不留著半吊子的連線
            throw;
        }

        _injector = injector;
        _gamepad = gamepad;
        _capture = capture;
        _audio = audio;

        session.MessageReceived += (channel, type, payload) =>
        {
            if (channel != Channel.Input) return;
            if (type == MessageType.Pad) gamepad.Handle(payload.Span);
            else injector.Handle(channel, type, payload.Span);
        };

        // VideoKeyframeRequest 走 Video channel(見 MessageType 定義),不是 Input。
        session.MessageReceived += (channel, type, _) =>
        {
            if (channel == Channel.Video && type == MessageType.VideoKeyframeRequest)
                capture.RequestKeyframe();
        };

        session.KeyReleaseRequested += _ => injector.ReleaseAll();

        session.StateChanged += state =>
        {
            if (state is RdpB.Core.Session.SessionState.Closed)
                StopStreaming();
        };

        _activeSession = session; // 到這裡才算真的在串流
        StateChanged?.Invoke();
    }

    /// <summary>session 已經關閉時送東西進去會同步丟 ObjectDisposedException——這是正常的
    /// 競態(Client 斷線那瞬間,擷取/音訊執行緒可能還在送下一包),不該讓呼叫端(CaptureEncoder/
    /// AudioCapturer 自己的背景執行緒)直接被炸死。</summary>
    private static void SafeSend(CoreSession session, Channel channel, MessageType type, byte[] payload)
    {
        try { _ = session.SendAsync(channel, type, payload); }
        catch (ObjectDisposedException) { }
        catch { }
    }

    private void StopStreaming()
    {
        _capture?.Dispose();
        _audio?.Dispose();
        _capture = null;
        _audio = null;
        _injector = null;
        _gamepad = null;
        _activeSession = null;
        ReleaseSlot();
        StateChanged?.Invoke();
    }

    private void ReleaseSlot() => Interlocked.Exchange(ref _slotClaimed, 0);

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _acceptLoopCts?.Cancel();
        _listener?.Dispose();

        if (_activeSession is not null)
            await _activeSession.DisposeAsync().ConfigureAwait(false);
        StopStreaming();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }

        _listener = null;
        Pin = null;
        Interlocked.Exchange(ref _connectedClientCount, 0);
        Interlocked.Exchange(ref _slotClaimed, 0);

        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
