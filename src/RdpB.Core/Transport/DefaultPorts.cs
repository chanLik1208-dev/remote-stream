namespace RdpB.Core.Transport;

/// <summary>
/// 唯一一份預設埠號清單。Kyra 實測抓到的真坑:HostListenerService 預設開 7443 聽 TCP+TLS,
/// 但 GUI 的連線測試(ConnectivityChecker)原本是對 3389 測 TCP,兩個數字各自寫死、對不上——
/// 測出來「連線成功」其實可能是連到對方機器上**真的 Windows RDP 服務**(3389 剛好是標準 RDP
/// 埠),不是真的連到 RDP-B 自己的 Host。所有用到預設埠的地方都要從這裡拿,不要各自硬編。
/// </summary>
public static class DefaultPorts
{
    /// <summary>Host 目前只開一個 TCP+TLS 監聽埠(QUIC 監聽還沒做,§3 待辦)。
    /// 連線測試、Host 面板的預設值都用這個,不會再各自飄掉。</summary>
    public const int Host = 7443;
}
