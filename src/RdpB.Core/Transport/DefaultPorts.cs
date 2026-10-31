namespace RdpB.Core.Transport;

/// <summary>
/// 唯一一份預設埠號清單。Kyra 實測抓到的真坑:HostListenerService 預設開 7443 聽 TCP+TLS,
/// 但 GUI 的連線測試(ConnectivityChecker)原本是對 3389 測 TCP,兩個數字各自寫死、對不上——
/// 測出來「連線成功」其實可能是連到對方機器上**真的 Windows RDP 服務**(3389 剛好是標準 RDP
/// 埠),不是真的連到 RDP-B 自己的 Host。所有用到預設埠的地方都要從這裡拿,不要各自硬編。
/// </summary>
public static class DefaultPorts
{
    /// <summary>Host 用同一個埠號同時開 TCP+TLS(真正的 TCP)和 QUIC(UDP)監聽——
    /// 兩種協定分屬不同的傳輸層,同一個數字不會互相衝突,不需要兩個不同的埠號設定。
    /// 連線測試、Host 面板的預設值都用這個,不會再各自飄掉。</summary>
    public const int Host = 7443;

    /// <summary>計畫 §7:RDP 外觀偽裝只套在 DIRECT 模式的 TCP 連線上,而且埠要跟外觀一致
    /// (DPI 對 3389 看到非 RDP 流量才顯眼,§7 原文)。走代理的連線不套偽裝,用
    /// <see cref="Host"/> 那個埠,跟這個分開。</summary>
    public const int RdpCamouflage = 3389;

    /// <summary>計畫 §1 的 WebSocket+TLS transport——用途是躲在 CDN/反向代理後面或走
    /// Xray fallback(見 <see cref="WsTransport"/> 上的註解),跟直連/代理的主埠分開,
    /// 不跟 §7 的偽裝埠衝突。</summary>
    public const int Ws = 7444;

    /// <summary>Kyra 要求的功能:自動下載內嵌的 mihomo(VLESS/VMess/Trojan/Hysteria2 等
    /// RdpB 自己不會講的協定,交給 mihomo 處理)本機 mixed-port——**純粹是這台 Client 機器
    /// 自己對自己用的本機埠**,不是 Host/Client 之間要協調的對外埠,所以不會跟上面幾個撞到
    /// (那幾個是「對外或對 Host」的埠,這個永遠只有 127.0.0.1 連得到)。見
    /// RdpB.App/Services/MihomoProxyService.cs。</summary>
    public const int Mihomo = 17890;

    /// <summary>mihomo 的 external-controller(REST API),只給 RdpB 自己查節點清單/
    /// 手動切換節點用——永遠只綁 127.0.0.1,不開 secret(本來就只有本機行程連得到,
    /// 跟 <see cref="Mihomo"/> 一樣是純本機用途)。</summary>
    public const int MihomoController = 17891;
}
