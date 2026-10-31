using System.Text;
using RdpB.App.Services;
using Xunit;

namespace RdpB.Tests;

public class ProxyNodeResolverTests
{
    [Fact]
    public void TryParseNodeUri_Socks5_NoCredentials()
    {
        Assert.True(ProxyNodeResolver.TryParseNodeUri("socks5://127.0.0.1:10808", out var node));
        Assert.Equal("socks5", node.Kind);
        Assert.Equal("127.0.0.1", node.Host);
        Assert.Equal(10808, node.Port);
        Assert.Null(node.Username);
        Assert.Null(node.Password);
    }

    [Fact]
    public void TryParseNodeUri_SocksAlias_NormalizesToSocks5()
    {
        Assert.True(ProxyNodeResolver.TryParseNodeUri("socks://example.com:1080", out var node));
        Assert.Equal("socks5", node.Kind);
        Assert.Equal("example.com", node.Host);
        Assert.Equal(1080, node.Port);
    }

    [Fact]
    public void TryParseNodeUri_WithCredentials_Extracted()
    {
        Assert.True(ProxyNodeResolver.TryParseNodeUri("socks5://kyra:p%40ss@proxy.example.com:1080", out var node));
        Assert.Equal("kyra", node.Username);
        Assert.Equal("p@ss", node.Password); // URL-escaped @ 要被還原
    }

    [Fact]
    public void TryParseNodeUri_Http_RecognizedAsHttpConnect()
    {
        Assert.True(ProxyNodeResolver.TryParseNodeUri("http://127.0.0.1:8080", out var node));
        Assert.Equal("http", node.Kind);
    }

    [Fact]
    public void TryParseNodeUri_UnsupportedScheme_ReturnsFalse()
    {
        Assert.False(ProxyNodeResolver.TryParseNodeUri("vmess://eyJ2IjoiMiJ9", out _));
    }

    [Fact]
    public void TryParseNodeUri_MissingPort_ReturnsFalse()
    {
        Assert.False(ProxyNodeResolver.TryParseNodeUri("socks5://127.0.0.1", out _));
    }

    [Fact]
    public void ResolveFromText_PlainTextWithSupportedNode_Succeeds()
    {
        var result = ProxyNodeResolver.ResolveFromText("socks5://127.0.0.1:10808\nhttp://1.2.3.4:8080");
        Assert.True(result.Success);
        Assert.Equal("socks5", result.Node!.Kind);
        Assert.Equal(10808, result.Node.Port);
    }

    [Fact]
    public void ResolveFromText_Base64EncodedSubscription_DecodesAndFindsNode()
    {
        var raw = "vmess://not-supported\nsocks5://10.0.0.1:1080\n";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));

        var result = ProxyNodeResolver.ResolveFromText(base64);

        Assert.True(result.Success);
        Assert.Equal("10.0.0.1", result.Node!.Host);
        Assert.Equal(1080, result.Node.Port);
        Assert.Contains("1", result.Message); // 提到有 1 個節點被略過(vmess)
    }

    [Fact]
    public void ResolveFromText_OnlyUnsupportedSchemes_ExplainsWhyNotABug()
    {
        var result = ProxyNodeResolver.ResolveFromText("vmess://aaa\nvless://bbb");
        Assert.False(result.Success);
        Assert.Contains("VMess", result.Message);
    }

    [Fact]
    public void ResolveFromText_NoNodeLikeContent_ReportsNothingFound()
    {
        var result = ProxyNodeResolver.ResolveFromText("這份內容裡什麼都沒有");
        Assert.False(result.Success);
        Assert.DoesNotContain("VMess", result.Message); // 不該誤報「格式不支援」,因為根本沒找到任何節點
    }

    [Fact]
    public void ResolveFromConfigFile_MissingFile_ReportsReadError()
    {
        var result = ProxyNodeResolver.ResolveFromConfigFile(Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.txt"));
        Assert.False(result.Success);
        Assert.Contains("讀不到", result.Message);
    }

    [Fact]
    public void ResolveFromConfigFile_RealFileWithNode_Succeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rdpb-proxy-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "socks5://192.168.1.1:1080");
        try
        {
            var result = ProxyNodeResolver.ResolveFromConfigFile(path);
            Assert.True(result.Success);
            Assert.Equal("192.168.1.1", result.Node!.Host);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
