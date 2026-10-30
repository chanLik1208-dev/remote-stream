using System.Text;
using System.Text.Json;

namespace RdpB.Core.Proxy;

/// <summary>
/// 解析訂閱連結抓下來的內容(計畫 §8 的「訂閱連結」欄位,之前只存不解析——
/// Kyra 回饋後決定內嵌 sing-box,這裡是「訂閱文字 → 節點清單」那一半,不碰網路、
/// 不碰 sing-box,純函式方便測試)。
///
/// 支援 vless / trojan / shadowsocks(SIP002 格式)/ vmess / socks5 / http 這幾種
/// 最常見的 URI scheme。訂閱內容本身可能是整段 base64,也可能就是明文一行一個 URI,
/// 兩種都處理。
/// </summary>
public static class SubscriptionParser
{
    public static IReadOnlyList<ProxyNode> Parse(string subscriptionContent)
    {
        var text = DecodeIfBase64(subscriptionContent.Trim());
        var nodes = new List<ProxyNode>();

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var node = TryParseLine(line);
            if (node is not null) nodes.Add(node);
        }

        return nodes;
    }

    private static ProxyNode? TryParseLine(string line)
    {
        try
        {
            if (line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                return ParseVmess(line);
            if (line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                return ParseUriStyle(line, "vless", useUserInfoAs: "uuid");
            if (line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                return ParseUriStyle(line, "trojan", useUserInfoAs: "password");
            if (line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
                return ParseShadowsocks(line);
            // hy2:// 是 hysteria2:// 常見的簡寫別名,兩種訂閱來源都看得到,都收。
            if (line.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
                return ParseUriStyle(line, "hysteria2", useUserInfoAs: "auth");
            if (line.StartsWith("socks://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
                return ParseUriStyle(line, "socks", useUserInfoAs: "userinfo");
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return ParseUriStyle(line, "http", useUserInfoAs: "userinfo");
        }
        catch
        {
            // 單一節點格式壞掉不該讓整份訂閱解析失敗——跳過這一行就好。
        }
        return null;
    }

    /// <summary>vless/trojan/socks/http 都是標準 URI 形狀:scheme://userinfo@host:port?query#name。</summary>
    private static ProxyNode ParseUriStyle(string line, string protocol, string useUserInfoAs)
    {
        var uri = new Uri(line);
        var name = string.IsNullOrEmpty(uri.Fragment) ? uri.Host : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

        var paramsDict = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(uri.UserInfo))
            paramsDict[useUserInfoAs] = Uri.UnescapeDataString(uri.UserInfo);

        foreach (var (key, value) in ParseQueryString(uri.Query))
            paramsDict[key] = value;

        int port = uri.Port > 0 ? uri.Port : protocol switch
        {
            "http" => 80,
            "https" => 443,
            _ => 0,
        };

        return new ProxyNode(protocol, name, uri.Host, port, paramsDict, line);
    }

    private static ProxyNode ParseVmess(string line)
    {
        var payload = line["vmess://".Length..];
        var json = DecodeBase64(payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string Get(string prop) => root.TryGetProperty(prop, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

        var name = Get("ps") is { Length: > 0 } ps ? ps : Get("add");
        var server = Get("add");
        int.TryParse(Get("port"), out var port);

        var paramsDict = new Dictionary<string, string>
        {
            ["id"] = Get("id"),
            ["aid"] = Get("aid"),
            ["net"] = Get("net"),
            ["type"] = Get("type"),
            ["host"] = Get("host"),
            ["path"] = Get("path"),
            ["tls"] = Get("tls"),
        };

        return new ProxyNode("vmess", name, server, port, paramsDict, line);
    }

    /// <summary>SIP002 格式:ss://BASE64(method:password)@host:port#name。
    /// 舊式「整段都 base64」的格式沒支援,算已知缺口,遇到會解析失敗、這個節點被跳過。</summary>
    private static ProxyNode ParseShadowsocks(string line)
    {
        var uri = new Uri(line);
        var decoded = DecodeBase64(Uri.UnescapeDataString(uri.UserInfo));
        var parts = decoded.Split(':', 2);
        var name = string.IsNullOrEmpty(uri.Fragment) ? uri.Host : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

        var paramsDict = new Dictionary<string, string>
        {
            ["method"] = parts.Length > 0 ? parts[0] : "",
            ["password"] = parts.Length > 1 ? parts[1] : "",
        };

        return new ProxyNode("shadowsocks", name, uri.Host, uri.Port, paramsDict, line);
    }

    /// <summary>不用 System.Web.HttpUtility——Core 要保持輕量、不多一個相依,查詢字串格式
    /// 又簡單到不值得為此多一個套件。</summary>
    private static IEnumerable<(string Key, string Value)> ParseQueryString(string query)
    {
        var q = query.TrimStart('?');
        if (q.Length == 0) yield break;

        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0)
            {
                yield return (Uri.UnescapeDataString(pair), "");
            }
            else
            {
                yield return (Uri.UnescapeDataString(pair[..idx]), Uri.UnescapeDataString(pair[(idx + 1)..]));
            }
        }
    }

    private static string DecodeIfBase64(string content)
    {
        // 明文訂閱(每行已經是 vmess://... 這種)就不用再 base64 解一次。
        if (content.Contains("://")) return content;
        return DecodeBase64(content);
    }

    private static string DecodeBase64(string s)
    {
        var normalized = s.Replace('-', '+').Replace('_', '/');
        int padding = (4 - normalized.Length % 4) % 4;
        normalized += new string('=', padding);
        var bytes = Convert.FromBase64String(normalized);
        return Encoding.UTF8.GetString(bytes);
    }
}
