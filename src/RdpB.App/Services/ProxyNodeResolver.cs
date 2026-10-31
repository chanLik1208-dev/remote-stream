using System.Net.Http;
using System.Text;

namespace RdpB.App.Services;

/// <summary>解析出來的一個可用節點——只有「RdpB 自己的 transport 真的看得懂」的節點才算數
/// (見類別上的註解:SOCKS5/HTTP CONNECT,不是 VMess/VLESS/Trojan)。</summary>
public sealed record ResolvedProxyNode(string Kind, string Host, int Port, string? Username, string? Password);

public sealed record ProxyResolveResult(bool Success, ResolvedProxyNode? Node, string Message);

/// <summary>
/// 計畫 §8 設定頁「連線 → 透過代理」那塊的真正落地——以前訂閱連結/設定檔路徑只負責存進
/// config.json,從來沒有真的解析過內容、抽出可以連的節點(UI 上那行「節點解析還沒做,
/// 目前先存起來」講的就是這個缺口)。
///
/// **刻意不做的事,老實講清楚**:「訂閱連結」這個詞在一般用語裡通常指 VMess/VLESS/Trojan/
/// Shadowsocks 這類節點(v2rayN/Clash 訂閱的那種),但 RdpB 自己的 <see cref="RdpB.Core.Transport.TcpTlsTransport"/>
/// 只握手 SOCKS5 和 HTTP CONNECT 這兩種標準協定(見計畫 §1「不能走 VLESS 等代理」的原始
/// 動機——這個方案本來就設計成「透過外部工具(v2rayN/Clash)開出一個本機 SOCKS5/HTTP
/// endpoint,RdpB 連去那個 endpoint」,不是自己重新實作一套 VMess/VLESS 協定客戶端,
/// 那是完全不同量級的工程)。所以這裡解析訂閱內容時,**只挑出 `socks5://`/`socks://`/
/// `http://`/`https://` 這幾種我們自己真的看得懂的 URI**;訂閱裡其他類型的節點(vmess://
/// 等)會被忽略,解析結果裡用白話文告訴使用者「訂閱裡有節點,但格式我們不支援,請改用
/// v2rayN/Clash 開一個本機 SOCKS5 再把那個位址填進來」,不要讓使用者誤以為解析失敗是
/// 我們的 bug。
/// </summary>
public static class ProxyNodeResolver
{
    private static readonly string[] SupportedSchemes = ["socks5://", "socks://", "http://", "https://"];

    public static async Task<ProxyResolveResult> ResolveFromSubscriptionAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ProxyResolveResult(false, null, "訂閱連結是空的");

        string content;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            content = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ProxyResolveResult(false, null, $"訂閱連結抓不到內容:{ex.Message}");
        }

        return ResolveFromText(content);
    }

    public static ProxyResolveResult ResolveFromConfigFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new ProxyResolveResult(false, null, "設定檔路徑是空的");

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new ProxyResolveResult(false, null, $"讀不到這個設定檔:{ex.Message}");
        }

        return ResolveFromText(content);
    }

    /// <summary>核心解析邏輯,訂閱連結抓到的內容/設定檔內容都走這裡——兩者的差別只在於
    /// 「內容從哪裡來」,解析規則完全一樣。公開是為了方便單元測試不用真的打網路/寫檔案。</summary>
    public static ProxyResolveResult ResolveFromText(string content)
    {
        var lines = SplitIntoLines(content);

        var supportedNode = TryFindSupportedNode(lines, out var ignoredCount);
        if (supportedNode is not null)
            return new ProxyResolveResult(true, supportedNode,
                $"解析成功:{supportedNode.Kind}://{supportedNode.Host}:{supportedNode.Port}" +
                (ignoredCount > 0 ? $"(另外還有 {ignoredCount} 個節點格式不支援,已略過)" : ""));

        bool hasAnyNodeLikeLine = lines.Any(l => l.Contains("://"));
        return new ProxyResolveResult(false, null, hasAnyNodeLikeLine
            ? "這份訂閱/設定檔裡的節點都不是 RdpB 支援的格式(只認得 socks5:// 和 http(s)://)——" +
              "RdpB 不會自己講 VMess/VLESS/Trojan 這些協定,請改用 v2rayN/Clash 之類的工具先在本機" +
              "開一個 SOCKS5 或 HTTP 代理 endpoint,再把那個位址(例如 socks5://127.0.0.1:10808)填進來"
            : "沒有在內容裡找到任何看起來像代理節點的東西(一行都沒有 \"://\")");
    }

    private static List<string> SplitIntoLines(string content)
    {
        content = content.Trim();

        // 訂閱連結的常見慣例:整份內容是 base64,解碼之後才是一行一個節點 URI 的純文字。
        // 先試著當 base64 解,解不出來或解出來不像文字就直接當純文字處理——兩種來源都接受,
        // 不用使用者自己先判斷訂閱是不是編碼過。
        if (TryDecodeBase64(content, out var decoded))
            content = decoded;

        return content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static bool TryDecodeBase64(string content, out string decoded)
    {
        decoded = "";
        // base64 字串不會有這些字元,混進明文 URI(有 : / .)的內容拿去解一定失敗,用這個
        // 當作快速篩選,避免把已經是純文字的訂閱內容硬塞進 base64 解碼器產生垃圾輸出。
        var compact = content.Replace("\r", "").Replace("\n", "");
        if (compact.Length == 0 || compact.Contains("://")) return false;

        try
        {
            var bytes = Convert.FromBase64String(PadBase64(compact));
            var text = Encoding.UTF8.GetString(bytes);
            // 解出來的東西要「看起來像文字」才算數(至少要出現一個 "://",不然很可能是
            // 真的 base64 編碼過的二進位資料,不是我們要的訂閱內容),不然寧可當作不是 base64。
            if (!text.Contains("://")) return false;
            decoded = text;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string PadBase64(string s) => s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');

    private static ResolvedProxyNode? TryFindSupportedNode(List<string> lines, out int ignoredCount)
    {
        ignoredCount = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;

            bool isSupportedScheme = SupportedSchemes.Any(s => line.StartsWith(s, StringComparison.OrdinalIgnoreCase));
            if (!isSupportedScheme)
            {
                if (line.Contains("://")) ignoredCount++;
                continue;
            }

            if (TryParseNodeUri(line, out var node))
                return node;

            ignoredCount++; // scheme 認得,但格式畸形(例如沒有 port),一樣當作略過繼續找下一個
        }
        return null;
    }

    /// <summary>解析單一節點 URI。公開給 <see cref="SettingsViewModel"/> 手動輸入代理網址那個
    /// 欄位共用——不管節點是從訂閱解析出來的,還是使用者自己手打的,都是同一套規則。</summary>
    public static bool TryParseNodeUri(string uriText, out ResolvedProxyNode node)
    {
        node = null!;
        // socks:// 是 socks5:// 的常見別名寫法,正規化成同一種 kind 再往下處理。
        string normalized = uriText.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)
            ? "socks5://" + uriText["socks://".Length..]
            : uriText;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return false;
        if (uri.Port <= 0 || uri.Port > 65535 || uri.Host.Length == 0) return false;

        string kind = uri.Scheme.ToLowerInvariant() switch
        {
            "socks5" => "socks5",
            "http" or "https" => "http",
            _ => "",
        };
        if (kind.Length == 0) return false;

        string? username = null, password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            username = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        }

        node = new ResolvedProxyNode(kind, uri.Host, uri.Port, username, password);
        return true;
    }
}
