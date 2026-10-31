using System.Net;
using RdpB.Core.Media;
using RdpB.Core.Pairing;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Core.Transport.Obfuscation;
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

    /// <summary>code review 複查時抓到的真坑:StopStreaming() 原本的 idempotency 守衛是
    /// check-then-act,沒有鎖——capture.Faulted(CaptureEncoder 自己的背景執行緒,經
    /// Task.Run 間接觸發 session.DisposeAsync → StopStreaming)跟使用者按「停止 Host」
    /// (UI 執行緒,StopAsync 裡先 await session.DisposeAsync 再自己明確呼叫一次
    /// StopStreaming)真的可以同時發生,兩條執行緒都讀到欄位非 null、都通過守衛,結果
    /// _capture/_audio 被 Dispose() 兩次。這把鎖只包「檢查、真的釋放資源、清欄位」這一段,
    /// StateChanged 的觸發特意留在鎖外,避免持鎖時跑外部事件處理程式碼。</summary>
    private readonly object _streamingLock = new();
    private TcpTlsTransportListener? _listener;
    private QuicTransportListener? _quicListener;
    private TcpTlsTransportListener? _rdpCamouflageListener;
    private WsTransportListener? _wsListener;
    private CancellationTokenSource? _acceptLoopCts;
    private Task? _acceptLoop;
    private Task? _quicAcceptLoop;
    private Task? _rdpCamouflageAcceptLoop;
    private Task? _wsAcceptLoop;
    // 建構時就一定會設好(見建構子),不再是「開了 Host 才有」的東西,型別不用再帶 ?。
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _hostCertificate;

    /// <summary>frp 隧道(見 FrpTunnelService 類別上的註解)——只有 Frp.Enabled 時才會真的
    /// 啟動,失敗(frpc.exe 下載不了、設定錯、frps 連不上)不該讓 Host 本身開不起來,
    /// 所以永遠用 try/catch 包起來,跟 QUIC/RDP 偽裝/WS 那三個次要監聽同一個待遇。</summary>
    private readonly FrpTunnelService _frp = new();

    /// <summary>0=空著,1=被佔用(真的在串流)。code review 抓到的真坑(第一版):原本只看
    /// `_activeSession is not null` 擋第二個人,但那個欄位要等配對「成功」才會設,兩個人在
    /// 同一個配對視窗內一起連進來,兩邊都通得過這個檢查,最後變成兩組 CaptureEncoder/
    /// AudioCapturer 互搶同一個螢幕/音效裝置。**後來改成一接受連線就先佔位**(在配對之前)
    /// ——結果紅隊自我攻擊測試抓到更嚴重的坑:攻擊者連上來什麼都不送,就能把這個獨佔資源
    /// 占到 PairingTimeout 逾時,0 成本癱瘓所有合法連線。現在改回**配對真的成功之後**才搶
    /// (用 Interlocked.CompareExchange,原子操作,不是 check-then-act,兩個合法使用者
    /// 同時配對成功那個原始坑一樣防得住)——配對過程本身不再佔用這個資源,但也因此失去了
    /// 原本「同時只能有一條連線在猜 PIN」這個意外的副作用,改用 <see cref="_pairingSlots"/>
    /// 明確限制並行配對數量,見那個欄位的註解。</summary>
    private int _slotClaimed;

    /// <summary>紅隊測試量出來的數字:PIN 完全沒有速率限制時,單執行緒序列猜測約
    /// 100 次/秒,窮舉 100 萬種組合約 2.76 小時。把獨佔槽位從「接受連線時」改成「配對成功
    /// 後」(見 _slotClaimed 的註解)會讓這件事更嚴重——沒有這個號誌的話,攻擊者可以同時
    /// 開很多條連線平行猜,把 2.76 小時除以並行數。這個號誌限制「同時間最多幾條連線在走
    /// PairingCoordinator.WaitForPairingAsync」,滿了直接拒絕新的配對嘗試(不排隊等,見用法)
    /// ——數字不用大,反正合法使用者同時間只有 1 個人在配對,這裡只是防止攻擊者平行化。</summary>
    private readonly SemaphoreSlim _pairingSlots = new(3, 3);

    /// <summary>計畫文件原本就寫「PIN 5 分鐘有效期」,但這行代碼一直沒有真的做——紅隊測試
    /// 量出來的 2.76 小時窮舉時間,正是建立在「PIN 永遠不過期」這個事實上。配合下面的
    /// _consecutiveWrongPin,兩者都會讓攻擊者沒辦法真的靠「反正有無限時間」慢慢窮舉完。</summary>
    private static readonly TimeSpan PinValidity = TimeSpan.FromMinutes(5);
    private DateTimeOffset _pinGeneratedAt;

    /// <summary>連續猜錯 PIN 太多次(不分哪條連線,累計)就直接作廢現在這組 PIN、重發一組
    /// 新的,並且在 <see cref="LockoutDuration"/> 這段時間內拒絕任何新的配對嘗試——逼攻擊者
    /// 每次都要從頭等,而不是對著同一組 PIN 慢慢試。數字(10 次)刻意抓得比「人類打錯一兩次」
    /// 寬鬆很多,不會不小心鎖到 Kyra 自己打錯字那幾次。</summary>
    private int _consecutiveWrongPin;
    private const int MaxConsecutiveWrongPin = 10;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromSeconds(30);
    private DateTimeOffset _pairingLockedUntil = DateTimeOffset.MinValue;

    private CoreSession? _activeSession;
    private CaptureEncoder? _capture;
    private AudioCapturer? _audio;
    private Injector? _injector;
    private GamepadSink? _gamepad;

    /// <summary>code review 抓到的坑:ConfigSync 原本定期重送是靠獨立的 System.Threading.Timer,
    /// 跟 Session 自己的心跳迴圈各轉各的——改成搭 Session.HeartbeatTick 這班車(見該事件上的
    /// 註解),這裡只需要記「訂閱了哪個 handler」才能在 StopStreaming 時正確取消訂閱。</summary>
    private Action? _configSyncHeartbeatHandler;
    private int _connectedClientCount;

    /// <summary>目前真的在串流的那個 Client 的指紋——撤銷信任(RevokeTrustedClient)要用
    /// 這個判斷「正在撤銷的是不是當下這個人」,是的話要立刻斷開,不能只是改掉 config.json
    /// 讓他下次連線才被擋。</summary>
    private string? _activeClientFingerprint;

    public bool IsRunning { get; private set; }
    public string? Pin { get; private set; }
    public int Port { get; private set; }
    public int ConnectedClientCount => _connectedClientCount;
    public bool IsStreaming => _activeSession is not null;
    public string? SelectedEncoder => _capture?.SelectedEncoder;

    /// <summary>「允許預匹配模式」要用的——Kyra 自己在 Host 這台機器上讀這個值(念出來、
    /// 截圖傳給自己、隨便哪種她信任的管道都行),貼到 Client 端新增裝置時的「預匹配指紋」
    /// 欄位,之後第一次連線就不是 TOFU,是直接嚴格比對。跟 Pin 不一樣:Pin 每次啟動 Host
    /// 都重新產生、5 分鐘後過期;這個指紋是跟著身分憑證走的,只要沒換過機器/沒重灌,
    /// 一直都是同一個值。</summary>
    public string? FingerprintHex { get; private set; }

    public bool FrpIsRunning => _frp.IsRunning;
    public string? FrpLastError => _frp.LastError;

    public event Action? StateChanged;

    /// <summary>配對成功了,但真的要開始串流(擷取/編碼)時失敗——診斷用,GUI 之後可以拿
    /// 這個在 Host 面板顯示白話文錯誤,而不是讓使用者乾等看不懂發生什麼事。</summary>
    public event Action<Exception>? StreamingStartFailed;

    public HostListenerService(string? configPath = null)
    {
        _configPath = configPath ?? ConfigStore.DefaultPath;

        // 指紋跟著身分憑證走,不是「Host 開著才有」的東西——建構時就把它算出來,Kyra 不用
        // 先把 Host 開起來才能看到自己的指紋去告訴 Client 端(預匹配模式,見 FingerprintHex
        // 屬性上的註解)。
        var certPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB", "host-identity.bin");
        _hostCertificate = CertificateStore.LoadOrCreate(certPath, Environment.MachineName);
        FingerprintHex = Convert.ToHexString(PairingService.ComputeFingerprint(_hostCertificate.RawData));

        _frp.StateChanged += () => StateChanged?.Invoke(); // 讓 GUI 能透過同一個事件看到 frp 狀態變化
    }

    public void Start(int port = DefaultPorts.Host)
    {
        if (IsRunning) return;

        _listener = TcpTlsTransportListener.Start(
            new IPEndPoint(IPAddress.Any, port), _hostCertificate, clientCertificateRequired: true);
        Port = _listener.BoundPort;

        // QUIC 監聽跟 TCP 綁**同一個埠號**(見 DefaultPorts.Host 的註解:UDP/TCP 是不同的
        // 傳輸層,同一個數字不衝突)——用 TCP 真正綁到的那個號碼(port=0 時 TCP 會拿到一個
        // 隨機埠),QUIC 這邊明確指定同一個號碼,不要自己也傳 0,否則兩邊會各自拿到不同號碼,
        // Client 端 TransportConnector 用同一個 Port 打兩種協定就會對不上。
        //
        // 真的有人回報過的坑:這裡原本完全沒有 try/catch、也沒有逾時,假設
        // QuicListener.ListenAsync 一定很快就會回來。實際上在某些機器上(msquic 初始化卡住、
        // 防火牆/VPN/某些網卡驅動搭配 UDP 綁定時有問題)這個呼叫會整個掛住不回來——而
        // Start() 又是被 HostViewModel 在 Avalonia UI 執行緒上同步呼叫,結果就是整個視窗
        // 「沒有回應」,使用者連「啟動 Host」這顆按鈕按下去都沒有任何回饋,只能強制關閉。
        // 加一個逾時(5 秒,QUIC listener 本來就該在本機 bind 完成,不該需要這麼久),逾時
        // 或任何例外都當成「這台機器上 QUIC 這條路暫時用不了」,跟 RDP 偽裝/WS 監聽一樣
        // 優雅跳過,不要讓整個 Host 開不起來,更不該讓整個 UI 卡死。
        try
        {
            using var quicStartCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _quicListener = QuicTransportListener.StartAsync(
                new IPEndPoint(IPAddress.Any, Port), _hostCertificate, quicStartCts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[HostListenerService] QUIC 監聽開不起來(5 秒逾時或例外),略過,改用 TCP+TLS:{ex.Message}");
            _quicListener = null;
        }

        Pin = PairingService.GeneratePin();
        _pinGeneratedAt = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _consecutiveWrongPin, 0);
        _pairingLockedUntil = DateTimeOffset.MinValue;
        Interlocked.Exchange(ref _connectedClientCount, 0);
        Interlocked.Exchange(ref _slotClaimed, 0);
        IsRunning = true;

        _acceptLoopCts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => TcpAcceptLoopAsync(_listener, _hostCertificate, _acceptLoopCts.Token));
        if (_quicListener is not null)
            _quicAcceptLoop = Task.Run(() => QuicAcceptLoopAsync(_quicListener, _hostCertificate, _acceptLoopCts.Token));

        // code review 抓到的真坑:這個方法原本對同一份 config.json 分開呼叫兩次
        // ConfigStore.Load(偽裝開關一次、frp 設定一次)——多一次磁碟讀檔+JSON 解析,
        // 讀一次重複用就好。
        var startupConfig = ConfigStore.Load(_configPath);

        // §7 的偽裝握手現在是 TcpTlsTransportListener 自動偵測的(peek 第一個 byte,見那個
        // 類別上的註解)——上面主監聽(Port)本來就已經能同時接受偽裝過的連線和一般直連,
        // 不用另外設定。這裡的 3389 專用監聽只在 Kyra 要求的「進階選項」開著時才多開:
        // 只有想要「連埠號都跟真正的 RDP 一致」這一點點額外效果的人才需要,預設不開就不會
        // 有撞上 Windows 原生 Remote Desktop 服務的風險(真的在 Kyra 機器上炸出來的坑)。
        if (startupConfig.Default.RdpCamouflageUseDedicatedPort)
        {
            try
            {
                _rdpCamouflageListener = TcpTlsTransportListener.Start(
                    new IPEndPoint(IPAddress.Any, DefaultPorts.RdpCamouflage), _hostCertificate,
                    clientCertificateRequired: true);
                _rdpCamouflageAcceptLoop = Task.Run(() =>
                    TcpAcceptLoopAsync(_rdpCamouflageListener, _hostCertificate, _acceptLoopCts.Token));
            }
            catch (Exception ex)
            {
                // 最常見的原因:3389 已經被真正的 Windows RDP 服務佔用。不是致命錯誤,
                // 只是這台機器上「連埠號都一致」這個進階效果用不了,主監聽的偽裝偵測不受影響。
                RdpB.Core.Diagnostics.AppLog.WriteLine($"[HostListenerService] RDP 偽裝專用監聽(3389)開不起來,略過:{ex.Message}");
                _rdpCamouflageListener = null;
            }
        }

        // §1 的 WebSocket+TLS——躲 CDN/反代用的,自己的埠號,跟上面幾個都獨立,一樣用
        // try/catch 包起來,開不起來(埠被佔用)不該讓整個 Host 連不起來。
        try
        {
            _wsListener = WsTransportListener.Start(
                new IPEndPoint(IPAddress.Any, DefaultPorts.Ws), _hostCertificate, clientCertificateRequired: true);
            _wsAcceptLoop = Task.Run(() => WsAcceptLoopAsync(_wsListener, _hostCertificate, _acceptLoopCts.Token));
        }
        catch (Exception ex)
        {
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[HostListenerService] WebSocket 監聽({DefaultPorts.Ws})開不起來,略過:{ex.Message}");
            _wsListener = null;
        }

        // frp 隧道(Kyra 要求的功能,見 FrpTunnelService 類別上的註解)——轉發的是主 TCP+TLS
        // 監聽埠(Port),不是 QUIC/偽裝/WS 那幾個。
        //
        // code review 抓到的真坑:原本的註解說「FrpTunnelService.Start 不會丟例外」,但
        // Start() 裡會讀 settings.Token,那個屬性背後是 DPAPI 解密(SecretProtector.
        // UnprotectFromBase64)——如果 config.json 被搬去別台機器或別個 Windows 帳號下
        // (無痕模式本來就設計成可以整份複製,一般模式的 config.json 理論上也可能被手動
        // 複製過去),DPAPI 解不開會丟 CryptographicException,而且是在上面幾個監聽都已經
        // 綁定成功、IsRunning=true 之後才丟出來——例外會一路飄出 Start(),讓呼叫端
        // (HostViewModel.StartAsync)看到「啟動失敗」,但其實 Host 已經真的在監聽、連得進來,
        // 畫面顯示的狀態跟實際狀態對不上。包一層 try/catch,跟其他次要功能一樣優雅降級。
        try
        {
            // instanceId 用身分憑證指紋前 12 碼——同一組 frps 帳號/token 被用在不只一台機器上
            // 時(真的在 Kyra 機器上發生過),proxy name 要不一樣,見 FrpTunnelService.Start
            // 上的註解。指紋本身已經是 hex 字串,直接截斷不需要再轉碼。
            var frpInstanceId = FingerprintHex is { Length: >= 12 } fp ? fp[..12] : "default";
            _frp.Start(startupConfig.Frp, Port, frpInstanceId);
        }
        catch (Exception ex)
        {
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[HostListenerService] frp 隧道啟動失敗,略過:{ex.Message}");
        }

        StateChanged?.Invoke();
    }

    private async Task TcpAcceptLoopAsync(
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

            await OnTransportAcceptedAsync(transport, transport.RemoteCertificate, hostCertificate, ct).ConfigureAwait(false);
        }
    }

    private async Task QuicAcceptLoopAsync(
        QuicTransportListener listener,
        System.Security.Cryptography.X509Certificates.X509Certificate2 hostCertificate,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QuicTransport transport;
            try
            {
                transport = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; } // 跟 TCP 那條迴圈一樣:單一連線失敗不拖累 accept loop

            await OnTransportAcceptedAsync(transport, transport.RemoteCertificate, hostCertificate, ct).ConfigureAwait(false);
        }
    }

    private async Task WsAcceptLoopAsync(
        WsTransportListener listener,
        System.Security.Cryptography.X509Certificates.X509Certificate2 hostCertificate,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WsTransport transport;
            try
            {
                transport = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; } // 跟其他幾條 accept loop 一樣:單一連線(例如 Upgrade 交握失敗)不拖累整條迴圈

            await OnTransportAcceptedAsync(transport, transport.RemoteCertificate, hostCertificate, ct).ConfigureAwait(false);
        }
    }

    /// <summary>TCP 和 QUIC 兩條 accept loop 共用的「接到一條新連線之後」邏輯——佔位、丟進
    /// <see cref="HandleNewConnectionAsync"/>。兩種 transport 都實作 <see cref="ITransport"/>,
    /// 差別只在憑證要從各自的具體型別上拿(見呼叫端),所以抽成這一個共用方法,不要兩份一樣的程式碼。</summary>
    private async Task OnTransportAcceptedAsync(
        ITransport transport,
        System.Security.Cryptography.X509Certificates.X509Certificate2? remoteCertificate,
        System.Security.Cryptography.X509Certificates.X509Certificate2 hostCertificate,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _connectedClientCount);
        StateChanged?.Invoke();

        // **紅隊自我攻擊測試抓到的真坑,比已知的 PIN 無限猜更嚴重**:原本在這裡就先佔位
        // (在配對「之前」),代價是攻擊者完全不用猜 PIN——連上來(TLS handshake 用任何
        // 自簽憑證都能過,身分判斷在更上層)之後什麼都不送,就能把 _slotClaimed 獨占到
        // PairingTimeout(60 秒)逾時才釋放,59 秒時主動斷線重連就能無限循環、永久擋住
        // 任何合法連線——而且連一次 HMAC 運算都不用做,比猜 PIN 便宜得多。
        //
        // 改成不在這裡佔位,接受連線本身不消耗這個獨佔資源,交給 HandleNewConnectionAsync
        // 在「配對真的成功」或「本來就已信任」之後,才去搶這把鎖(用 Interlocked
        // CompareExchange,原子操作,不是 check-then-act)——這樣多個人同時在「等待配對」
        // 完全不會互相卡,只有真正要開始串流這一刻才會序列化,原本要防的那個「兩邊都配對
        // 成功、搶同一組 CaptureEncoder/AudioCapturer」的坑一樣防得住。
        //
        // 代價(刻意接受、沒有解決的部分):這樣確實允許同時存在多條「等待配對」的連線,
        // 理論上還是可能被大量並行連線灌爆執行緒/socket 資源——但那是任何暴露在網路上的
        // TCP 服務都要面對的「連線洪水」問題,不是這裡特有的設計缺陷,也不是免費的攻擊
        // (真的要開很多條連線、佔很多資源),跟原本那個「一條連線、不用發一個 byte」就能
        // 癱瘓所有人的洞完全不是同一個等級,先不在這次範圍內處理(之後要做可以加 per-IP
        // 的併發連線數上限)。
        _ = HandleNewConnectionAsync(transport, remoteCertificate, hostCertificate, ct);
    }

    private async Task HandleNewConnectionAsync(
        ITransport transport,
        System.Security.Cryptography.X509Certificates.X509Certificate2? remoteCertificate,
        System.Security.Cryptography.X509Certificates.X509Certificate2 hostCertificate,
        CancellationToken ct)
    {
        if (remoteCertificate is null)
        {
            try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
            return;
        }

        // **真的坑,不是這次新測試才碰到的意外抖動**:這幾個檢查(讀 config.json 判斷
        // 信不信任、PIN 過期、並行配對名額)原本排在 `new CoreSession(transport)` 之後——
        // 但 transport 的讀取迴圈(FrameStreamPump)其實從 AcceptAsync 一 accept 到連線
        // 就已經開始跑了,跟我們接下來做什麼完全無關。如果 Client 端動作夠快(本機測試,
        // 或任何不用等人手動輸入 PIN 的自動化流程),Client 送出的 Pair 訊息可能剛好在
        // 這幾個檢查(尤其 IsTrusted 裡那個同步的磁碟讀檔)跑完、
        // PairingCoordinator.WaitForPairingAsync 真的訂閱 session.MessageReceived 之前
        // 就已經送到——這個事件在那個當下完全沒有訂閱者,訊息就這樣憑空消失,Host 只能
        // 乾等整整 60 秒的 PairingTimeout 逾時,不是幾毫秒內就能分辨出 PIN 對不對。
        // 新加的並行配對名額檢查又讓這個縫隙更寬了一點,讓這個原本就存在但少見的競態
        // 變得常態化(拿自己寫的安全性測試連續猜 10 次 PIN,結果每次都卡滿 60 秒,
        // 12 分鐘才跑完就是這樣抓到的)。
        //
        // 修法:所有不需要 session 的檢查(算指紋、查信任清單、鎖定期、PIN 過期、搶配對
        // 名額)全部搬到 session 真的建出來「之前」,把「建立 session(建構子裡就會
        // 訂閱 transport 的 MessageReceived)」跟「呼叫 WaitForPairingAsync(同步訂閱
        // session 自己的 MessageReceived)」中間完全不再夾雜任何 await——這中間的縫隙
        // 只剩下兩個建構子等級的同步呼叫,Client 不可能在這麼短的時間內就把 TLS 加密過的
        // 訊息送達並處理完。
        var clientFingerprintHex = Convert.ToHexString(
            PairingService.ComputeFingerprint(remoteCertificate.RawData));

        bool alreadyTrusted = IsTrusted(clientFingerprintHex);

        if (!alreadyTrusted)
        {
            // 鎖定中(連續猜錯太多次剛觸發的冷卻期)——直接拒絕,連 _pairingSlots 的名額
            // 都不用佔,也還不需要真的建立 session。
            if (DateTimeOffset.UtcNow < _pairingLockedUntil)
            {
                try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
                return;
            }

            // PIN 過期(計畫文件寫的 5 分鐘,之前從來沒真的實作過)——自動換一組新的,
            // 這條連線手上的 PIN(不管是 Kyra 自己放太久沒用,還是攻擊者截到的舊值)這次
            // 一定會比對失敗,逼對方要嘛放棄要嘛重新取得當下這組。
            if (DateTimeOffset.UtcNow - _pinGeneratedAt > PinValidity)
                RegeneratePin();

            // 並行配對上限(見 _pairingSlots 上的註解)——滿了直接拒絕,不排隊等,免得
            // 變成另一種「反正連線會堆積」的資源耗盡手法。
            if (!await _pairingSlots.WaitAsync(0, ct).ConfigureAwait(false))
            {
                try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
                return;
            }
        }

        // 到這裡才建立 session——前面那些會真的花時間的檢查都做完了,接下來到
        // WaitForPairingAsync 真的訂閱上之間不能再有任何 await。
        var session = new CoreSession(transport);
        session.Start();

        if (!alreadyTrusted)
        {
            PairingResult result;
            try
            {
                // timeout 直接交給 PairingCoordinator 自己處理,不用在這裡手動包一層
                // CancellationTokenSource——ct 仍然負責「Host 整個被叫停」這件事。
                (result, _) = await PairingCoordinator.WaitForPairingAsync(
                    session, Pin ?? "", remoteCertificate, hostCertificate, ct, PairingTimeout).ConfigureAwait(false);
            }
            finally
            {
                _pairingSlots.Release();
            }

            if (result != PairingResult.Success)
            {
                if (result == PairingResult.WrongPin)
                {
                    int wrongCount = Interlocked.Increment(ref _consecutiveWrongPin);
                    if (wrongCount >= MaxConsecutiveWrongPin)
                    {
                        _pairingLockedUntil = DateTimeOffset.UtcNow + LockoutDuration;
                        RegeneratePin(); // 連續猜錯太多次,現在這組 PIN 直接作廢,攻擊者要從頭來過
                    }
                }
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            Interlocked.Exchange(ref _consecutiveWrongPin, 0); // 配對成功,之前的錯誤次數不該繼續累計
            RecordTrustedClient(clientFingerprintHex, isNew: true);
        }
        else
        {
            // Kyra 要求的無人值守加固:「加上密碼的無人值守比較好,每次都有驗證(信任裝置
            // 需要 Host 上設定的密碼)」——純指紋釘選對「機器旁邊沒有人」的情境來說不夠,
            // 私鑰外洩就是永久性的破口,沒有人在電腦前能察覺。開了這個設定的話,即使是
            // 已經信任的裝置,每次連線也要再驗一次這組連線密碼,跟 PIN 配對完全同一套
            // HMAC 挑戰/回應機制(PairingCoordinator.WaitForPairingAsync),只是把「PIN」
            // 換成「Host 上設定的連線密碼」——不是重新配對(不會動 TrustedClients 清單),
            // 單純多一道「現在這個人知不知道密碼」的驗證。
            var connectionPassword = ConfigStore.Load(_configPath).ConnectionPassword;
            if (connectionPassword is { Enabled: true, Password: { Length: > 0 } password })
            {
                if (!await _pairingSlots.WaitAsync(0, ct).ConfigureAwait(false))
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                PairingResult passwordResult;
                try
                {
                    (passwordResult, _) = await PairingCoordinator.WaitForPairingAsync(
                        session, password, remoteCertificate, hostCertificate, ct, PairingTimeout).ConfigureAwait(false);
                }
                finally
                {
                    _pairingSlots.Release();
                }

                if (passwordResult != PairingResult.Success)
                {
                    // 真的在這台機器上炸出來的坑:這條路徑(已信任裝置重連,Host 開了連線
                    // 密碼)如果 Client 端那台裝置忘了勾選「每次連線都需要連線密碼」,Client
                    // 根本不知道要送密碼、也不會主動等任何回應——它會直接把連線當成功,
                    // 串流視窗先開出來。Host 這裡卻在乾等一個永遠不會送來的密碼,逾時
                    // (PairingTimeout,可能 1~2 分鐘)才斷線,而且完全沒告訴 Client 原因,
                    // 使用者看到的是「一開始好像連上了,過一陣子莫名其妙斷線」。
                    // MessageType.Bye 本來就保留給這種「我要關了,附上原因」的情境,只是從來
                    // 沒真的用過——送一次,Client 收到後至少能顯示清楚的錯誤訊息,不用自己
                    // 猜為什麼斷線。Control channel 是可靠的,這裡不用等 ACK,斷線前能送多少
                    // 算多少。
                    var reason = passwordResult == PairingResult.Timeout
                        ? "連線密碼驗證逾時——這台裝置可能沒有在「編輯裝置」裡勾選「每次連線都需要連線密碼」,導致一直沒有送出密碼"
                        : "連線密碼不正確";
                    await SendByeAsync(session, reason, ct).ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }

            RecordTrustedClient(clientFingerprintHex, isNew: false);
        }

        // code review 抓到的坑:配對(可能花到 60 秒)期間,使用者可能已經按了停止 Host——
        // 這裡要再檢查一次,不然 StopAsync() 以為乾淨停掉了,背地裡卻又冒出一個真的在跑的
        // 擷取器,變成使用者看不到、關不掉的殭屍串流。
        if (ct.IsCancellationRequested)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // 真正搶獨佔槽位的時機:配對已經成功(或本來就信任)之後,這一刻才去搶——原子操作,
        // 不是 check-then-act。多條連線都配對成功時(原本那個真坑:兩個合法使用者在同一個
        // 配對視窗內都連進來),只有先搶到的那一個會繼續往下開始串流,其他的乾淨斷線,
        // 不會變成兩組 CaptureEncoder/AudioCapturer 互搶同一組硬體資源。
        if (Interlocked.CompareExchange(ref _slotClaimed, 1, 0) != 0)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            // code review 抓到的真坑:_activeClientFingerprint 原本是在這裡、
            // StartStreamingForAsync 回傳「之後」才設,而 _activeSession 是在
            // StartStreamingForAsync 內部設的——兩個寫入中間有一個小縫隙,兩個又都沒鎖
            // (RevokeTrustedClient 讀的時候也沒鎖)。如果 Kyra 剛好在這個縫隙裡對同一個
            // fingerprint 呼叫 RevokeTrustedClient,這時 _activeClientFingerprint 還是
            // null,比對不會命中,剛配對成功的這個(已經被撤銷的)連線反而不會被斷開,
            // 違背「撤銷要立刻生效」這個方法自己承諾的語意。改成 fingerprint 直接傳進
            // StartStreamingForAsync,跟 _activeSession 在同一把 _streamingLock 裡一起設。
            await StartStreamingForAsync(session, clientFingerprintHex, ct).ConfigureAwait(false);
            // 成功:slot 留著佔用,交給 StopStreaming() 之後釋放。
        }
        catch (Exception ex)
        {
            // 已經在 StartStreamingForAsync 裡把 session 關掉、資源釋放乾淨了,這裡只需要
            // 補釋放剛剛搶到的槽位——不要讓例外一路往外飄,砸了 accept loop(呼叫端是
            // fire-and-forget)。
            ReleaseSlot();
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[HostListenerService] 串流啟動失敗:{ex}");
            StreamingStartFailed?.Invoke(ex);
        }
    }

    /// <summary>手動或自動(過期/連續猜錯太多次)換一組新 PIN。公開給 GUI 一個「重新產生」
    /// 按鈕用——例如 Kyra 覺得剛剛那組 PIN 可能被看到了,想立刻作廢重發,不用等 5 分鐘
    /// 自然過期。只有 Host 正在跑的時候有意義。</summary>
    public void RegeneratePin()
    {
        if (!IsRunning) return;
        Pin = PairingService.GeneratePin();
        _pinGeneratedAt = DateTimeOffset.UtcNow;
        StateChanged?.Invoke();
    }

    /// <summary>給 GUI 的「已信任裝置」清單用——每次都重新讀磁碟,不快取,確保看到的是
    /// 最新狀態(跟這個 class 其他讀 config 的地方一致的作法)。</summary>
    public IReadOnlyList<TrustedClientRecord> GetTrustedClients()
    {
        lock (_configLock)
        {
            return ConfigStore.Load(_configPath).TrustedClients
                .OrderByDescending(c => c.LastConnected)
                .ToList();
        }
    }

    /// <summary>撤銷一個已信任裝置——紅隊測試跟手動安全審查都點名的缺口:指紋釘選機制
    /// 本身對「私鑰外洩」沒有防禦力(攻擊者直接用偷到的私鑰做出合法的 mTLS 握手,協定
    /// 看到的就是「正確的憑證」),唯一的事後補救就是撤銷。從 TrustedClients 清單移除,
    /// 下次這個指紋連線時會被當成沒配對過,要重新走一次 PIN 流程才能再信任。如果正好是
    /// 現在這個在串流的人,不能只改設定檔等它下次連線才生效——直接把目前的 session 斷開,
    /// 撤銷要立刻生效,不是下一次連線才生效。</summary>
    public void RevokeTrustedClient(string fingerprintHex)
    {
        lock (_configLock)
        {
            var config = ConfigStore.Load(_configPath);
            config.TrustedClients.RemoveAll(c =>
                string.Equals(c.FingerprintHex, fingerprintHex, StringComparison.OrdinalIgnoreCase));
            ConfigStore.Save(config, _configPath);
        }

        // code review 抓到的真坑:_activeClientFingerprint/_activeSession 這兩個欄位的寫入
        // 現在已經包進 _streamingLock 裡一起設(見 StartStreamingForAsync),這裡讀的時候
        // 也要用同一把鎖,不然還是可能讀到「兩個欄位設到一半」的中間狀態。
        CoreSession? sessionToDisconnect = null;
        lock (_streamingLock)
        {
            if (string.Equals(_activeClientFingerprint, fingerprintHex, StringComparison.OrdinalIgnoreCase))
                sessionToDisconnect = _activeSession;
        }
        if (sessionToDisconnect is not null)
        {
            _ = sessionToDisconnect.DisposeAsync().AsTask(); // 觸發 StateChanged(Closed) → StopStreaming(),見那兩個方法上的註解
        }

        StateChanged?.Invoke();
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

    private async Task StartStreamingForAsync(CoreSession session, string clientFingerprintHex, CancellationToken ct)
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
        // 音訊掛了不該拖累整條串流(刻意的設計決定,不是漏寫),但完全不留痕跡會讓真正
        // 除錯音訊問題的人一點線索都沒有——跟上面 SafeSend 那個坑是同一個道理,補上記錄。
        audio.Faulted += ex => RdpB.Core.Diagnostics.AppLog.WriteLine($"[HostListenerService] 音訊擷取掛了(不影響畫面串流):{ex}");

        // 注意:IsStreaming/_activeSession 一定要等 capture.Start() 真的成功才設——不然
        // Start() 丟例外、或還沒跑完的這段空檔,IsStreaming 會騙人說已經在串流了(code review
        // 風格的坑,寫整合測試時自己先踩到:host.IsStreaming 量到 true,但 SelectedEncoder
        // 還是 null,兩個訊號不同步)。
        ct.ThrowIfCancellationRequested(); // 配對期間 Host 可能已經被叫停,見呼叫端的註解
        try
        {
            capture.Start(monitorIndex: 0, fps: quality.Fps, bitrateKbps: quality.BitrateKbps, scalePercent: quality.ScalePercent);
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

        // 計畫 §4/§8 HUD「協議/編解碼細節」那一塊——Client 自己的解碼器種類/實際解析度
        // 自己量得到,只有「Host 用的是哪個編碼器、目標 fps/位元率」這幾個數字只有 Host
        // 自己知道,串流一開始(capture.Start() 真的成功之後,SelectedEncoder 才有值)
        // 送給 Client。
        //
        // 真的在這台機器上炸出來的坑:原本只送這一次,跟 PairingCoordinator 的 Pair 訊息
        // 修過的坑一模一樣——Client 端訂閱 session.MessageReceived 的時機如果剛好跟這次送達
        // 沒接上(競態窗口很窄,但不是不可能),這個訊息就這樣憑空消失,而且因為只送一次,
        // 之後永遠不會再補送,HUD 的「Host 編碼器」欄位會卡在「量測中…」一輩子,即使畫面
        // 其他部分(影像)都已經透過別的機制(VideoKeyframeRequest 自我修復)恢復正常。
        // 改成跟心跳一樣,定期重送(Control channel 是可靠的,重送的訊息很小,不會造成
        // 負擔)——不只修掉這個競態,之後如果 Kyra 想做「串流中即時改畫質」也不用另外加
        // 一條「設定變更了要通知 Client」的機制,這個定時重送本來就會把新數字帶過去。
        var streamInfo = new HostStreamInfo(capture.SelectedEncoder ?? "未知", quality.Fps, quality.BitrateKbps);
        SafeSend(session, Channel.Control, MessageType.ConfigSync, streamInfo.ToBytes());

        // 搭 Session 心跳(預設 500ms 一次)這班車,每 10 個 tick≈5 秒重送一次——不用再另開
        // 一個獨立的 Timer(見 _configSyncHeartbeatHandler 欄位上的註解)。
        const int ticksPerResend = 10;
        int tickCount = 0;
        _configSyncHeartbeatHandler = () =>
        {
            if (++tickCount < ticksPerResend) return;
            tickCount = 0;
            var info = new HostStreamInfo(capture.SelectedEncoder ?? "未知", quality.Fps, quality.BitrateKbps);
            SafeSend(session, Channel.Control, MessageType.ConfigSync, info.ToBytes());
        };
        session.HeartbeatTick += _configSyncHeartbeatHandler;

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

        lock (_streamingLock)
        {
            _activeSession = session; // 到這裡才算真的在串流
            _activeClientFingerprint = clientFingerprintHex; // 跟上面同一把鎖,RevokeTrustedClient 讀的時候才不會看到只設了一半
        }
        StateChanged?.Invoke();
    }

    /// <summary>session 已經關閉時送東西進去會同步丟 ObjectDisposedException——這是正常的
    /// 競態(Client 斷線那瞬間,擷取/音訊執行緒可能還在送下一包),不該讓呼叫端(CaptureEncoder/
    /// AudioCapturer 自己的背景執行緒)直接被炸死。</summary>
    private static void SafeSend(CoreSession session, Channel channel, MessageType type, byte[] payload)
    {
        try { _ = session.SendAsync(channel, type, payload); }
        catch (ObjectDisposedException) { } // 預期中的競態,見方法上的註解
        catch (Exception ex)
        {
            // code review 複查時發現的同一類坑:這裡原本吞掉任何其他例外,一個字都不留——
            // 跟上面那行「預期中的競態」不一樣,這個分支代表真的出乎意料的錯誤,安靜吞掉
            // 等於沒有任何線索可以查。還是不能讓它砸死 CaptureEncoder/AudioCapturer 的背景
            // 執行緒(這裡的語意本來就是「send 失敗就算了,串流會自己繼續」),但至少留個痕跡。
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[HostListenerService] SafeSend 遇到非預期的例外:{ex}");
        }
    }

    /// <summary>主動關閉連線前,先告訴對方原因——<see cref="MessageType.Bye"/> 這個訊息型別
    /// 本來就是為了這個情境保留的(見協定定義上的註解),只是一直沒有真的用過。跟
    /// <see cref="SafeSend"/> 不同,這裡要真的 await 送出去再繼續(不是 fire-and-forget),
    /// 因為呼叫端緊接著就會呼叫 session.DisposeAsync()——如果不等送完就開始關閉 transport,
    /// 這個「我要關了,原因是…」的訊息很可能自己先被關閉流程吃掉,白寫了。失敗就算了(對方
    /// 本來就要斷線了),不用讓這裡的例外往外飄。</summary>
    private static async Task SendByeAsync(CoreSession session, string reason, CancellationToken ct)
    {
        try
        {
            await session.SendAsync(Channel.Control, MessageType.Bye,
                System.Text.Encoding.UTF8.GetBytes(reason), ct).ConfigureAwait(false);
        }
        catch { /* 對方反正要斷線了,送不出去就算了,不是這裡要處理的錯誤 */ }
    }

    /// <summary>跟 Session.EnterReconnectingOrClosed 同一類坑(code review 複查時特地找的):
    /// StopAsync() 先 await _activeSession.DisposeAsync(),這會觸發 Session 的
    /// StateChanged(Closed) → 上面接的 handler 已經呼叫過一次這裡;接著 StopAsync() 自己
    /// 又無條件呼叫一次。欄位全部 null-safe 所以不會真的壞掉,但 StateChanged?.Invoke()
    /// 會白白多通知一次 GUI——沒有東西真的改變,卻讓 HostViewModel 以為狀態又變了。
    /// 一樣用「已經清過了就不要再動」當守衛,讓這個方法 idempotent。</summary>
    private void StopStreaming()
    {
        lock (_streamingLock)
        {
            if (_activeSession is null && _capture is null && _audio is null) return;

            if (_configSyncHeartbeatHandler is not null && _activeSession is not null)
                _activeSession.HeartbeatTick -= _configSyncHeartbeatHandler;
            _configSyncHeartbeatHandler = null;
            _capture?.Dispose();
            _audio?.Dispose();
            _capture = null;
            _audio = null;
            _injector = null;
            _gamepad = null;
            _activeSession = null;
            _activeClientFingerprint = null;
            ReleaseSlot();
        }

        // 贏得上面那把鎖的呼叫者才會走到這裡——保證只通知一次,不管是哪個執行緒先到。
        StateChanged?.Invoke();
    }

    private void ReleaseSlot() => Interlocked.Exchange(ref _slotClaimed, 0);

    private static void TryCleanup(string stepName, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[HostListenerService] 停止 Host 時「{stepName}」清理失敗(不影響其他步驟繼續執行):{ex.Message}");
        }
    }

    private static async Task TryCleanupAsync(string stepName, Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex)
        {
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[HostListenerService] 停止 Host 時「{stepName}」清理失敗(不影響其他步驟繼續執行):{ex.Message}");
        }
    }

    /// <summary>Kyra 回報「停止 Host 不會收回 frpc」的真坑:以前這個方法裡 `_listener.Dispose()`/
    /// `_quicListener.DisposeAsync()`/`_frp.StopAsync()` 這幾步一個接一個直接呼叫,中間完全
    /// 沒有 try/catch——QUIC 這條路這個 session 一直不太穩定(msquic 在某些機器/某些狀態下
    /// DisposeAsync 丟例外不是沒發生過),只要前面任何一步丟例外,**後面的 `_frp.StopAsync()`
    /// 整行根本不會執行到**,frpc.exe 就這樣被漏掉,不是哪個步驟邏輯寫錯,是前面一步出錯
    /// 擋住了後面全部的清理。每個清理步驟理論上互不相干(各自是獨立的資源),不該因為其中
    /// 一個失敗就拖累其他全部沒做——改成每一步都獨立包 try/catch,失敗只記 log,不影響
    /// 其他步驟繼續執行。</summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _acceptLoopCts?.Cancel();
        TryCleanup("主監聽(TCP+TLS)", () => _listener?.Dispose());
        TryCleanup("RDP 偽裝專用監聽", () => _rdpCamouflageListener?.Dispose());
        TryCleanup("WebSocket 監聽", () => _wsListener?.Dispose());
        if (_quicListener is not null)
            await TryCleanupAsync("QUIC 監聽", () => _quicListener.DisposeAsync().AsTask()).ConfigureAwait(false);
        // 不留著孤兒 frpc.exe 行程繼續連 frps——這一步不能因為上面任何一步失敗就被跳過,
        // 所以每一步都獨立 try/catch,不是疊在同一條沒有防護的執行路徑上。
        await TryCleanupAsync("frp 隧道", () => _frp.StopAsync()).ConfigureAwait(false);

        if (_activeSession is not null)
            await TryCleanupAsync("Session", () => _activeSession.DisposeAsync().AsTask()).ConfigureAwait(false);
        StopStreaming();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        if (_quicAcceptLoop is not null)
        {
            try { await _quicAcceptLoop.ConfigureAwait(false); } catch { }
        }
        if (_rdpCamouflageAcceptLoop is not null)
        {
            try { await _rdpCamouflageAcceptLoop.ConfigureAwait(false); } catch { }
        }
        if (_wsAcceptLoop is not null)
        {
            try { await _wsAcceptLoop.ConfigureAwait(false); } catch { }
        }

        _listener = null;
        _quicListener = null;
        _rdpCamouflageListener = null;
        _wsListener = null;
        Pin = null;
        Interlocked.Exchange(ref _connectedClientCount, 0);
        Interlocked.Exchange(ref _slotClaimed, 0);

        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
