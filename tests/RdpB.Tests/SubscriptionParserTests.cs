using System.Text;
using RdpB.Core.Proxy;

namespace RdpB.Tests;

/// <summary>對應 Kyra 的回饋:一個訂閱連結裡混著好幾種協議的節點,這裡測「訂閱文字 →
/// 節點清單」這一半的解析邏輯,不碰網路、不碰 sing-box。</summary>
public class SubscriptionParserTests
{
    [Fact]
    public void Parse_Vless_ExtractsUuidHostPortAndParams()
    {
        var uri = "vless://a1b2c3d4-0000-1111-2222-333344445555@example.com:443" +
                   "?encryption=none&security=tls&type=ws&path=%2Fws#我的節點";

        var nodes = SubscriptionParser.Parse(uri);

        var node = Assert.Single(nodes);
        Assert.Equal("vless", node.Protocol);
        Assert.Equal("example.com", node.Server);
        Assert.Equal(443, node.Port);
        Assert.Equal("我的節點", node.Name);
        Assert.Equal("a1b2c3d4-0000-1111-2222-333344445555", node.Params["uuid"]);
        Assert.Equal("tls", node.Params["security"]);
        Assert.Equal("ws", node.Params["type"]);
        Assert.Equal("/ws", node.Params["path"]);
    }

    [Fact]
    public void Parse_Trojan_ExtractsPasswordHostPort()
    {
        var uri = "trojan://my-secret-password@203.0.113.9:443?sni=example.com#Trojan-HK";

        var node = Assert.Single(SubscriptionParser.Parse(uri));
        Assert.Equal("trojan", node.Protocol);
        Assert.Equal("my-secret-password", node.Params["password"]);
        Assert.Equal("203.0.113.9", node.Server);
        Assert.Equal(443, node.Port);
        Assert.Equal("Trojan-HK", node.Name);
    }

    [Fact]
    public void Parse_ShadowsocksSip002_DecodesMethodAndPassword()
    {
        var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:hunter2"));
        var uri = $"ss://{userInfo}@198.51.100.4:8388#SS-JP";

        var node = Assert.Single(SubscriptionParser.Parse(uri));
        Assert.Equal("shadowsocks", node.Protocol);
        Assert.Equal("aes-256-gcm", node.Params["method"]);
        Assert.Equal("hunter2", node.Params["password"]);
        Assert.Equal("198.51.100.4", node.Server);
        Assert.Equal(8388, node.Port);
        Assert.Equal("SS-JP", node.Name);
    }

    [Fact]
    public void Parse_Vmess_DecodesBase64JsonPayload()
    {
        var json = """{"v":"2","ps":"VMess-SG","add":"192.0.2.1","port":"10086","id":"uuid-here","aid":"0","net":"ws","type":"none","host":"","path":"/vm","tls":""}""";
        var uri = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var node = Assert.Single(SubscriptionParser.Parse(uri));
        Assert.Equal("vmess", node.Protocol);
        Assert.Equal("VMess-SG", node.Name);
        Assert.Equal("192.0.2.1", node.Server);
        Assert.Equal(10086, node.Port);
        Assert.Equal("uuid-here", node.Params["id"]);
        Assert.Equal("ws", node.Params["net"]);
        Assert.Equal("/vm", node.Params["path"]);
    }

    [Fact]
    public void Parse_MultipleProtocolsInOneSubscription_ReturnsAllNodes()
    {
        var vless = "vless://uuid@a.example.com:443?security=tls#A";
        var trojan = "trojan://pass@b.example.com:443#B";
        var ss = $"ss://{Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:pw"))}@c.example.com:8388#C";

        var subscription = string.Join('\n', vless, trojan, ss);
        var nodes = SubscriptionParser.Parse(subscription);

        Assert.Equal(3, nodes.Count);
        Assert.Equal(["vless", "trojan", "shadowsocks"], nodes.Select(n => n.Protocol).ToArray());
    }

    [Fact]
    public void Parse_WholeSubscriptionBase64Encoded_DecodesFirst()
    {
        var plain = "vless://uuid@a.example.com:443?security=tls#A\ntrojan://pass@b.example.com:443#B";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));

        var nodes = SubscriptionParser.Parse(encoded);

        Assert.Equal(2, nodes.Count);
    }

    [Fact]
    public void Parse_OneBadLine_DoesNotStopParsingTheRest()
    {
        var subscription = string.Join('\n',
            "vless://uuid@a.example.com:443?security=tls#A",
            "vmess://not-valid-base64-json!!!",
            "trojan://pass@b.example.com:443#B");

        var nodes = SubscriptionParser.Parse(subscription);

        Assert.Equal(2, nodes.Count); // 壞掉的 vmess 那行被跳過,不影響前後兩個
        Assert.Equal(["vless", "trojan"], nodes.Select(n => n.Protocol).ToArray());
    }

    [Fact]
    public void Parse_EmptySubscription_ReturnsEmptyList()
    {
        Assert.Empty(SubscriptionParser.Parse(""));
    }

    [Fact]
    public void Parse_UnsupportedScheme_IsSkipped()
    {
        var nodes = SubscriptionParser.Parse("tuic://whatever@host:443#X");
        Assert.Empty(nodes); // 老實的已知缺口:還沒支援 tuic,安靜跳過不是崩潰
    }

    [Fact]
    public void Parse_Hysteria2_ExtractsAuthHostPortAndObfsParams()
    {
        var uri = "hysteria2://my-auth-password@203.0.113.50:443" +
                   "?insecure=1&sni=example.com&obfs=salamander&obfs-password=secret#HY2-US";

        var node = Assert.Single(SubscriptionParser.Parse(uri));
        Assert.Equal("hysteria2", node.Protocol);
        Assert.Equal("203.0.113.50", node.Server);
        Assert.Equal(443, node.Port);
        Assert.Equal("HY2-US", node.Name);
        Assert.Equal("my-auth-password", node.Params["auth"]);
        Assert.Equal("example.com", node.Params["sni"]);
        Assert.Equal("salamander", node.Params["obfs"]);
        Assert.Equal("secret", node.Params["obfs-password"]);
    }

    [Fact]
    public void Parse_Hy2Alias_IsTreatedSameAsHysteria2()
    {
        var uri = "hy2://pw@198.51.100.20:443?sni=example.com#HY2-Alias";

        var node = Assert.Single(SubscriptionParser.Parse(uri));
        Assert.Equal("hysteria2", node.Protocol);
        Assert.Equal("pw", node.Params["auth"]);
    }
}
