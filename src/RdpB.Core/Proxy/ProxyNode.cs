namespace RdpB.Core.Proxy;

/// <summary>一個訂閱連結解析出來的節點。Kyra 的回饋:一個訂閱連結裡通常混著
/// vmess/vless/trojan/shadowsocks 好幾種協議,不是只有一種——這個型別故意把
/// 協議特有的欄位都塞進 <see cref="Params"/>,不幫每種協議各開一個子類別,
/// 因為實際要怎麼用這些欄位是 sing-box 設定檔產生器的事,不是這個型別的事。</summary>
public sealed record ProxyNode(
    string Protocol,   // vmess|vless|trojan|shadowsocks|socks|http
    string Name,
    string Server,
    int Port,
    IReadOnlyDictionary<string, string> Params,
    string RawUri);
