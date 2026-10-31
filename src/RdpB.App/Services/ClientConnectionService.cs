using System.Security.Cryptography.X509Certificates;
using RdpB.Client;
using RdpB.Core.Pairing;
using RdpB.Core.Transport;
using RdpB.Core.Transport.Proxy;
using RdpB.Core.Windows;
using CoreSession = RdpB.Core.Session.Session;

namespace RdpB.App.Services;

public enum ConnectOutcome { Success, PairingCancelled, WrongPin, CertificateMismatch, PairingFailed, ConnectFailed }

/// <summary><paramref name="Window"/> 只有 <see cref="ConnectOutcome.Success"/> 才會有值——
/// 呼叫端(測試,或之後想做「視窗管理」的 GUI 功能)可以用它追蹤/關閉這次開出來的串流視窗,
/// 不用整個方法都不回傳任何東西。</summary>
public sealed record ConnectResultInfo(ConnectOutcome Outcome, string Message, StreamWindow? Window = null);

/// <summary>
/// 計畫 §8「連線」按鈕背後真正的邏輯——跟 ViewModel/Avalonia 完全分開,方便之後寫測試。
/// 串連:連線(mTLS)→ 沒配對過就跑 §2 的 PIN 配對流程 → 配對過就驗證指紋沒變 → 開
/// <see cref="CoreSession"/> → 交給 <see cref="StreamWindow"/> 真正把串流畫面開出來。
///
/// 透過 <see cref="TransportConnector"/> 的 Auto 模式連線:先試 QUIC(1.5 秒逾時),連不上
/// 自動退回 TCP+TLS——現在 <see cref="HostListenerService"/> 的 Host 端已經同時開
/// QUIC(UDP)和 TCP+TLS 監聽在同一個埠號上(見 <see cref="RdpB.Core.Transport.DefaultPorts.Host"/>
/// 的註解),不用再繞過 <see cref="TransportConnector"/> 自己直接打 TCP。
/// </summary>
public sealed class ClientConnectionService
{
    private readonly RdpBConfig _config;
    private readonly string _configPath;

    public ClientConnectionService(string? configPath = null)
    {
        _configPath = configPath ?? ConfigStore.DefaultPath;
        _config = ConfigStore.Load(_configPath);
    }

    /// <summary>
    /// <paramref name="requestPin"/>:沒配對過時用來跳出 PIN 輸入畫面,回傳 null 表示使用者取消。
    /// <paramref name="requestConnectionPassword"/>:<see cref="PinnedHostRecord.RequiresConnectionPassword"/>
    /// 開著時,已經信任的裝置也要每次連線都問一次 Host 上設定的連線密碼——留空就用
    /// <paramref name="requestPin"/> 頂替(同一個對話框,呼叫端懶得分開問也沒關係,畢竟
    /// UI 層面看起來是同一種「輸入一段文字」的互動)。
    /// 這個類別完全不碰 Avalonia,UI 怎麼問 PIN/密碼由呼叫端決定。
    /// </summary>
    public async Task<ConnectResultInfo> ConnectAsync(
        PinnedHostRecord record,
        Func<Task<string?>> requestPin,
        Func<Task<string?>>? requestConnectionPassword = null,
        CancellationToken ct = default)
    {
        var (host, port) = ViewModels.MainWindowViewModel.ParseHostAndPort(record.HostAddress);

        // 每台 Host 可以分開設(計畫 §8),沒設就用全域預設——跟 §7 的偽裝開關共用同一份設定。
        var settings = record.Overrides ?? _config.Default;
        var mode = ParseTransportMode(settings.Mode);
        var proxy = settings.ProxyUrl is { Length: > 0 } ? ParseProxy(settings.ProxyUrl, settings) : null;
        var clientCertPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB", "client-identity.bin");
        var clientCert = CertificateStore.LoadOrCreate(clientCertPath, Environment.MachineName + "-client");

        // 「知道指紋」跟「PIN 配對真的做完」是兩件事,分開判斷(§「允許預匹配模式」)。
        // needsPinExchange 預設邏輯跟這個欄位出現以前完全一樣:沒指紋就要配對。只有在
        // PinPairingPending 被明確設成 true(預匹配模式,見 MainWindowViewModel.AddDevice)
        // 時才會「有指紋但還是要配對」——這個旗標預設 false,忘記設的呼叫端只會落回舊行為,
        // 不會不小心把還沒配對過的連線當成已經信任。
        bool knowsFingerprint = record.FingerprintHex.Length > 0;
        bool needsPinExchange = !knowsFingerprint || record.PinPairingPending;

        ITransport transport;
        X509Certificate2? remoteCertificate;
        TransportKind transportKind;
        try
        {
            var connectResult = await TransportConnector.ConnectAsync(
                mode, host, quicPort: port, tcpPort: port, proxy,
                validateServerCertificate: cert => ValidateServerCertificate(cert, record, knowsFingerprint),
                ct: ct,
                clientCertificate: clientCert,
                enableRdpCamouflage: settings.RdpCamouflageWhenDirect,
                useDedicatedCamouflagePort: settings.RdpCamouflageUseDedicatedPort,
                disableQuic: settings.DisableQuic).ConfigureAwait(false);
            transport = connectResult.Transport;
            transportKind = connectResult.Kind;
            remoteCertificate = transport switch
            {
                TcpTlsTransport tcp => tcp.RemoteCertificate,
                QuicTransport quic => quic.RemoteCertificate,
                WsTransport ws => ws.RemoteCertificate,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            return new ConnectResultInfo(ConnectOutcome.ConnectFailed, $"連不上 {record.Name}:{ex.Message}");
        }

        // §7:偽裝只在沒代理、開關開著、而且真的落到 TCP 時才算有套用(QUIC 連得上就沒機會
        // 用到偽裝那條路,Ws 模式自己不套偽裝——見 TransportConnector/WsTransport 上的註解)。
        // 只是給 HUD 顯示用,不影響連線邏輯本身。
        bool usedRdpCamouflage = proxy is null && settings.RdpCamouflageWhenDirect && transportKind == TransportKind.Tcp;

        var session = new CoreSession(transport);
        session.Start();

        if (needsPinExchange)
        {
            var pin = await requestPin().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pin))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new ConnectResultInfo(ConnectOutcome.PairingCancelled, "已取消配對");
            }

            // code review 複查抓到的坑:這裡原本沒包 try/catch——PairAsync 理論上已經把
            // 自己這端能預期的失敗都轉成 PairingResult 了,但萬一哪天漏接了什麼例外型別
            // (剛修過一次 ObjectDisposedException 太窄的問題),不該讓它整個往外飄、砸穿
            // 「連線」按鈕,讓使用者看到的是程式完全沒反應,而不是一個講得出原因的錯誤訊息
            // ——跟 HostViewModel.StartAsync 之前補的那個坑是同一個道理。
            PairingResult pairResult;
            try
            {
                pairResult = await PairingCoordinator.PairAsync(
                    session, pin.Trim(), remoteCertificate!, clientCert, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new ConnectResultInfo(ConnectOutcome.ConnectFailed, $"配對過程中斷線:{ex.Message}");
            }

            if (pairResult != PairingResult.Success)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new ConnectResultInfo(pairResult switch
                {
                    PairingResult.WrongPin => ConnectOutcome.WrongPin,
                    PairingResult.CertificateMismatch => ConnectOutcome.CertificateMismatch,
                    _ => ConnectOutcome.PairingFailed,
                }, DescribePairingFailure(pairResult));
            }

            // 預匹配模式下這裡其實是把 record.FingerprintHex 填回同一個值(TLS 層早就已經
            // 嚴格比對過,能走到這裡代表一定相等)——沒預匹配的話才是第一次真正填上去。
            record.FingerprintHex = Convert.ToHexString(PairingService.ComputeFingerprint(remoteCertificate!.RawData));
            record.PinPairingPending = false; // 配對做完了,之後重連不用再問 PIN
        }
        else if (record.RequiresConnectionPassword)
        {
            // Kyra 要求的無人值守加固(見 PinnedHostRecord.RequiresConnectionPassword 上的
            // 註解):已經信任的裝置,Host 開了連線密碼的話,每次連線還是要再問一次——跟上面
            // needsPinExchange 那段幾乎一模一樣的流程(同一套 PairAsync HMAC 機制),差別只在
            // 這裡失敗了**不能**清掉 record.FingerprintHex(不是重新配對失敗,是已經信任的
            // 裝置這次密碼答錯,指紋釘選本身沒有問題,不該跟著被動到)。
            var passwordPrompt = requestConnectionPassword ?? requestPin;
            var password = await passwordPrompt().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(password))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new ConnectResultInfo(ConnectOutcome.PairingCancelled, "已取消輸入連線密碼");
            }

            PairingResult passwordResult;
            try
            {
                passwordResult = await PairingCoordinator.PairAsync(
                    session, password.Trim(), remoteCertificate!, clientCert, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new ConnectResultInfo(ConnectOutcome.ConnectFailed, $"連線密碼驗證過程中斷線:{ex.Message}");
            }

            if (passwordResult != PairingResult.Success)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new ConnectResultInfo(passwordResult switch
                {
                    PairingResult.WrongPin => ConnectOutcome.WrongPin,
                    PairingResult.CertificateMismatch => ConnectOutcome.CertificateMismatch,
                    _ => ConnectOutcome.PairingFailed,
                }, passwordResult == PairingResult.WrongPin ? "連線密碼不正確" : DescribePairingFailure(passwordResult));
            }
        }

        record.LastConnected = DateTimeOffset.UtcNow;
        PersistPairingResult(record);

        var window = new StreamWindow(session, $"RDP-B — {record.Name}",
            transportKind: transportKind.ToString(), usedProxy: proxy is not null, usedRdpCamouflage: usedRdpCamouflage,
            blockScreenCapture: _config.Privacy.BlockScreenCapture, detectTampering: _config.Privacy.DetectTampering,
            hudPreset: _config.Hud.Preset, hudPosition: _config.Hud.Position,
            hudOpacity: _config.Hud.Opacity, hudFontSize: _config.Hud.FontSize, hudUpdateHz: _config.Hud.UpdateHz);
        window.Show();

        return new ConnectResultInfo(ConnectOutcome.Success, $"已連線到 {record.Name}", window);
    }

    /// <summary>
    /// **真的坑,不是順手加的防禦性寫法**:這個類別建構時載入的 <see cref="_config"/> 只是
    /// 為了讀 <c>Default.ProxyUrl</c>——跟呼叫端(MainWindowViewModel)手上那份、record 實際
    /// 活在裡面的 <c>RdpBConfig</c> 是完全不同的物件實例。如果直接 <c>ConfigStore.Save(_config, _configPath)</c>,
    /// 存進去的是「這個服務自己建構時讀到的那份舊快照」,record 剛剛配對成功填上去的
    /// FingerprintHex 根本不在那份快照的 PinnedHosts 裡——等於白做,甚至可能拿舊快照覆蓋掉
    /// GUI 那邊真正的最新狀態。改成重新從磁碟讀一次最新的 config,按 HostAddress 找到(或建立)
    /// 對應的項目再存回去,不依賴呼叫端手上那份物件實例是不是「最新」的。</summary>
    private void PersistPairingResult(PinnedHostRecord record)
    {
        var config = ConfigStore.Load(_configPath);
        var existing = config.PinnedHosts.FirstOrDefault(h => h.HostAddress == record.HostAddress);
        if (existing is null)
        {
            config.PinnedHosts.Add(new PinnedHostRecord
            {
                Name = record.Name,
                HostAddress = record.HostAddress,
                FingerprintHex = record.FingerprintHex,
                LastConnected = record.LastConnected,
                PinPairingPending = record.PinPairingPending,
            });
        }
        else
        {
            existing.FingerprintHex = record.FingerprintHex;
            existing.LastConnected = record.LastConnected;
            existing.PinPairingPending = record.PinPairingPending;
        }
        ConfigStore.Save(config, _configPath);
    }

    /// <summary>已經配對過的裝置:TLS handshake 時對方出示的憑證指紋,一定要跟
    /// <see cref="PinnedHostRecord.FingerprintHex"/> 記的一致,不一致直接拒絕連線(可能是
    /// 重裝過系統、或真的有人冒充)。沒配對過的裝置:這裡先接受任何憑證,真正的信任建立在
    /// 之後的 PIN 配對流程(計畫 §2)——那一步才是密碼學上真正綁定身分的地方,TLS 層先接受
    /// 只是為了能把連線建立起來談 PIN,不是最終的信任判斷。</summary>
    private static bool ValidateServerCertificate(X509Certificate2? cert, PinnedHostRecord record, bool alreadyPaired)
    {
        if (!alreadyPaired) return true;
        if (cert is null) return false;

        var actualFingerprint = Convert.ToHexString(PairingService.ComputeFingerprint(cert.RawData));
        return string.Equals(actualFingerprint, record.FingerprintHex, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribePairingFailure(PairingResult result) => result switch
    {
        PairingResult.WrongPin => "PIN 不正確",
        PairingResult.CertificateMismatch => "憑證指紋不符,可能是重裝過系統或連線被竄改,需要重新配對",
        PairingResult.Timeout => "配對逾時,對方沒有在時限內回應",
        PairingResult.Disconnected => "配對過程中斷線了",
        _ => "配對失敗",
    };

    /// <summary>GUI 只讓人選 DIRECT / PROXY(見 TransportSettings.Mode 的註解),但底層
    /// Core 的 TransportMode 還有 auto/tcp/ws 這幾個值——"tcp" 當代理模式用(直接走 TCP,
    /// 不試 QUIC),其他值都當 auto,不認得的字串不該讓連線直接爆炸。</summary>
    private static TransportMode ParseTransportMode(string mode) => mode switch
    {
        "direct" => TransportMode.Direct,
        "tcp" => TransportMode.Tcp,
        "ws" => TransportMode.Ws,
        _ => TransportMode.Auto,
    };

    /// <summary>code review 風格的坑,抓到就順手修掉:這裡以前只認 <c>socks5://host:port</c>
    /// (不支援 http(s)://、不支援 URI 裡帶 user:pass@、也完全沒讀 <see cref="TransportSettings.ProxyUsername"/>/
    /// <see cref="TransportSettings.ProxyPassword"/>——那兩個欄位從 DPAPI 解密出來之後其實
    /// 從來沒有被用在任何地方建過 <see cref="ProxyOptions.Socks5Credentials"/>/
    /// <see cref="ProxyOptions.HttpProxyAuthorizationHeader"/>,等於整組代理認證形同虛設)。
    /// 改成跟 <see cref="ProxyNodeResolver.TryParseNodeUri"/>(節點解析用的同一套規則)共用
    /// 解析邏輯——URL 裡沒帶 user:pass@ 的話,才補上設定裡存的那組帳密。</summary>
    private static ProxyOptions? ParseProxy(string url, TransportSettings settings)
    {
        if (!ProxyNodeResolver.TryParseNodeUri(url, out var node)) return null;

        string? username = node.Username ?? settings.ProxyUsername;
        string? password = node.Password ?? settings.ProxyPassword;

        return node.Kind switch
        {
            "socks5" => new ProxyOptions(ProxyKind.Socks5, node.Host, node.Port,
                Socks5Credentials: username is { Length: > 0 } ? new Socks5Credentials(username, password ?? "") : null),
            "http" => new ProxyOptions(ProxyKind.HttpConnect, node.Host, node.Port,
                HttpProxyAuthorizationHeader: username is { Length: > 0 }
                    ? "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"))
                    : null),
            _ => null,
        };
    }
}
