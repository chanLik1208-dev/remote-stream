using System.Security.Cryptography.X509Certificates;
using RdpB.Core.Transport.Obfuscation;

namespace RdpB.Core.Transport;

public enum TransportMode { Auto, Direct, Tcp, Ws }

public enum TransportKind { Quic, Tcp, Ws }

/// <summary>連線結果,帶著「最後實際用了哪一種」給 §8 診斷頁/HUD 用,不用另外猜。</summary>
public sealed record ConnectResult(ITransport Transport, TransportKind Kind, string? FallbackReason);

/// <summary>
/// 計畫 §1「自動選擇」:先試 QUIC,1.5 秒內連不上就退回 TCP。指定走代理時直接走 TCP/WS
/// (QUIC 是 UDP,一般 SOCKS5/HTTP CONNECT 代理不轉發 UDP,所以有代理就不試 QUIC)。
/// </summary>
public static class TransportConnector
{
    private static readonly TimeSpan QuicAttemptTimeout = TimeSpan.FromSeconds(1.5);

    public static async Task<ConnectResult> ConnectAsync(
        TransportMode mode,
        string host,
        int quicPort,
        int tcpPort,
        ProxyOptions? proxy,
        Func<X509Certificate2?, bool> validateServerCertificate,
        CancellationToken ct = default,
        X509Certificate2? clientCertificate = null,
        int rdpCamouflagePort = DefaultPorts.RdpCamouflage,
        bool enableRdpCamouflage = true,
        bool useDedicatedCamouflagePort = false,
        int wsPort = DefaultPorts.Ws,
        bool disableQuic = false)
    {
        if (mode == TransportMode.Direct && proxy is not null)
            throw new InvalidOperationException("DIRECT 模式不應該帶代理設定(§6:DIRECT 一律不讀代理)");

        // Kyra 要求的開關:走 frp/DDNS 這類「中間一定有東西不轉發 UDP」的連線,QUIC 永遠連不上,
        // 每次都白等 QuicAttemptTimeout 才退回 TCP——disableQuic 讓 DIRECT/Auto 直接跳過 QUIC,
        // 但連線意圖仍然是 DIRECT,§7 的偽裝邏輯不受影響(跟選 PROXY 模式不一樣,那個是連
        // 偽裝都一起關掉)。
        bool shouldTryQuic = proxy is null && mode is TransportMode.Auto or TransportMode.Direct && !disableQuic;

        // code review 抓到的坑:「意圖是 DIRECT/Auto,所以走 isDirectContext:true 的直連 TCP」
        // 這個呼叫以前被原封不動複製了三份(QUIC 失敗退回的 Auto 分支、Direct 分支、
        // disableQuic 跳過 QUIC 的分支),只有 fallbackReason 不一樣——之後任何一個會動到
        // isDirectContext/偽裝埠邏輯的修改,很容易改到其中一兩份卻漏掉第三份,原地重現
        // 下面那段大註解正想避免的「isDirectContext 悄悄變 false」那類 bug。抽成一個
        // local function,只有一份真相。
        Task<ConnectResult> ConnectDirectAsync(string? fallbackReason) =>
            ConnectTcpAsync(host, tcpPort, null, validateServerCertificate,
                fallbackReason, clientCertificate, rdpCamouflagePort, isDirectContext: true,
                enableRdpCamouflage, useDedicatedCamouflagePort, ct);

        if (shouldTryQuic)
        {
            using var quicCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quicCts.CancelAfter(QuicAttemptTimeout);
            try
            {
                var quic = await QuicTransport.ConnectAsync(host, quicPort, validateServerCertificate, quicCts.Token, clientCertificate)
                    .ConfigureAwait(false);
                return new ConnectResult(quic, TransportKind.Quic, null);
            }
            catch (Exception ex) when (mode is TransportMode.Auto or TransportMode.Direct)
            {
                // Auto/Direct 都一樣:QUIC 失敗直接往下走 TCP+TLS(還是 DIRECT,只是換協定)。
                // isDirectContext: true(在 ConnectDirectAsync 裡)——這個分支只有在 shouldTryQuic
                // 成立時才會走到,而 shouldTryQuic 本身已經要求 proxy is null 且 mode 是
                // Auto/Direct,是真正的直連意圖,偽裝該不該套用交給 enableRdpCamouflage
                // (使用者的開關)決定。
                ct.ThrowIfCancellationRequested();
                return await ConnectDirectAsync(DescribeQuicFailure(ex)).ConfigureAwait(false);
            }
        }

        // disableQuic 讓我們跳過了上面 shouldTryQuic 那個區塊,但如果使用者的意圖本來就是
        // DIRECT/Auto(只是不想試 QUIC),連線仍然要走「直連」那條路(§7 偽裝照常可以套用)——
        // 不能讓它掉進下面那個「proxy/ws」專用的 switch,那邊的預設分支會把 isDirectContext
        // 寫死成 false,等於意外把偽裝關掉(見那個分支上的大段註解,這正是它想避免的誤判,
        // 不要在這裡重蹈覆轍)。
        if (proxy is null && mode is TransportMode.Auto or TransportMode.Direct)
        {
            return await ConnectDirectAsync(null).ConfigureAwait(false);
        }

        return mode switch
        {
            // WS 不套 §7 的偽裝(見 WsTransport 類別上的註解:躲 CDN/反代是 WS 自己的「偽裝」,
            // 不疊加 RDP 外觀那一層),可以照舊帶代理(SOCKS5/HTTP CONNECT 本來就轉發 TCP)。
            TransportMode.Ws => new ConnectResult(
                await WsTransport.ConnectAsync(host, wsPort, proxy, validateServerCertificate, ct, clientCertificate)
                    .ConfigureAwait(false),
                TransportKind.Ws, null),
            // 真的在 Kyra 機器上炸出來的坑:這個分支是 GUI 選「透過代理(PROXY)」時落到的路徑
            // (見 SettingsViewModel.Save:選 PROXY 存成 Mode="tcp")——但代理節點解析(訂閱連結/
            // 設定檔匯入)現在還沒做,UI 上選了 PROXY 之後 ProxyUrl 實際上還是 null。原本這裡
            // 傳進 ConnectTcpAsync 的 isDirectContext 如果只看「proxy 物件是不是 null」來決定
            // 要不要套 §7 的 RDP 偽裝,就會把「使用者選了 PROXY 但還沒設定節點」誤判成
            // 「沒有代理,走 DIRECT」,偷偷把連線埠從使用者填的埠(例如 7443)換成偽裝埠 3389——
            // 而 3389 不一定是 RdpB 自己的偽裝監聽器(常見:被 Windows 本機的 Remote Desktop
            // 服務佔用),對方收到看不懂的握手直接斷線,表現成「連不上,ECONNRESET」,訊息
            // 完全看不出真正原因。§6/§7 說得很清楚「偽裝的生效條件是沒有走代理」——這裡的
            // 「沒有走代理」指的是使用者的**意圖**(DIRECT/Auto),不是「proxy 物件剛好是 null」,
            // 這個分支本來就代表使用者明確選了非 DIRECT 的模式(PROXY 或 WS),一律不套偽裝,
            // 不管 proxy 物件有沒有真的解析出東西。
            _ => await ConnectTcpAsync(host, tcpPort, proxy, validateServerCertificate, fallbackReason: null,
                clientCertificate, rdpCamouflagePort, isDirectContext: false, enableRdpCamouflage, useDedicatedCamouflagePort, ct).ConfigureAwait(false),
        };
    }

    /// <summary>計畫 §6/§7:偽裝的生效條件是「使用者的連線意圖是 DIRECT/Auto 落到直連」——
    /// 一旦是 PROXY 模式就自動關閉,兩者互斥不疊加,**不是**單純看 <paramref name="proxy"/>
    /// 物件是不是 null(見上面呼叫端 switch 分支上的註解,這是真的在 Kyra 機器上炸出來的坑:
    /// PROXY 模式但代理節點還沒解析出來,proxy 物件也會是 null)。<paramref name="isDirectContext"/>
    /// 由呼叫端明確傳入——QUIC 失敗後退回 TCP 的那兩個呼叫點,本來就只在 Direct/Auto 模式下才會
    /// 走到,所以一定是 true;switch 分支則代表 PROXY/WS 之類的非直連意圖,一律傳 false。
    ///
    /// **預設(<paramref name="useDedicatedCamouflagePort"/> = false,Kyra 要求的改動)**:
    /// 偽裝握手直接疊在使用者指定的 <paramref name="port"/> 上——Host 端的
    /// <see cref="TcpTlsTransportListener"/> 現在會自動偵測每個連線是不是套了偽裝(peek 第一個
    /// byte),不需要另外佔用一個專用埠才能用,也就沒有撞埠風險,不需要退路重試。
    ///
    /// **進階選項(<paramref name="useDedicatedCamouflagePort"/> = true)**:照 §7 原文連去
    /// <paramref name="rdpCamouflagePort"/>(預設 3389,「連埠號都跟真正的 RDP 一致」這個額外
    /// 效果),但保留原文自己講好的「退路」:這條路被打斷(最常見:3389 被 Windows 自己的
    /// Remote Desktop 服務佔用)就自動退回 <paramref name="port"/>、不套偽裝再試一次——真的
    /// 在 Kyra 機器上炸出來的坑,原本完全沒做這一步,偽裝一失敗整個連線就宣告失敗。</summary>
    private static async Task<ConnectResult> ConnectTcpAsync(
        string host, int port, ProxyOptions? proxy,
        Func<X509Certificate2?, bool> validateServerCertificate,
        string? fallbackReason, X509Certificate2? clientCertificate,
        int rdpCamouflagePort, bool isDirectContext, bool enableRdpCamouflage, bool useDedicatedCamouflagePort,
        CancellationToken ct)
    {
        bool shouldObfuscate = isDirectContext && enableRdpCamouflage;
        if (!shouldObfuscate)
        {
            var plain = await TcpTlsTransport.ConnectAsync(host, port, proxy, validateServerCertificate, ct,
                obfuscator: null, clientCertificate: clientCertificate).ConfigureAwait(false);
            return new ConnectResult(plain, TransportKind.Tcp, fallbackReason);
        }

        if (!useDedicatedCamouflagePort)
        {
            var camouflaged = await TcpTlsTransport.ConnectAsync(host, port, proxy, validateServerCertificate, ct,
                obfuscator: new RdpCamouflageObfuscator(), clientCertificate: clientCertificate).ConfigureAwait(false);
            return new ConnectResult(camouflaged, TransportKind.Tcp, fallbackReason);
        }

        try
        {
            var camouflaged = await TcpTlsTransport.ConnectAsync(host, rdpCamouflagePort, proxy, validateServerCertificate, ct,
                obfuscator: new RdpCamouflageObfuscator(), clientCertificate: clientCertificate).ConfigureAwait(false);
            return new ConnectResult(camouflaged, TransportKind.Tcp, fallbackReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            var plain = await TcpTlsTransport.ConnectAsync(host, port, proxy, validateServerCertificate, ct,
                obfuscator: null, clientCertificate: clientCertificate).ConfigureAwait(false);
            return new ConnectResult(plain, TransportKind.Tcp,
                fallbackReason ?? $"RDP 偽裝專用埠連線失敗,已自動改用一般 TCP+TLS 直連:{ex.Message}");
        }
    }

    private static string DescribeQuicFailure(Exception ex) => ex switch
    {
        OperationCanceledException => $"QUIC 在 {QuicAttemptTimeout.TotalSeconds:0.#} 秒內沒有回應",
        _ => $"QUIC 連線失敗:{ex.Message}",
    };
}
