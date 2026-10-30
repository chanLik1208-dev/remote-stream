using RdpB.App.ViewModels;
using RdpB.Core.Transport;

namespace RdpB.Tests;

/// <summary>對應 Kyra 實測回饋:打「IP:port」整串被當成主機名去解析,連不出去。</summary>
public class MainWindowViewModelAddressParsingTests
{
    [Theory]
    [InlineData("192.168.0.120", "192.168.0.120", DefaultPorts.Host)]
    [InlineData("192.168.0.120:7443", "192.168.0.120", 7443)]
    [InlineData("192.168.0.120:9999", "192.168.0.120", 9999)]
    [InlineData(" 192.168.0.120 ", "192.168.0.120", DefaultPorts.Host)] // 前後空白要修掉
    [InlineData("example.com", "example.com", DefaultPorts.Host)]
    [InlineData("example.com:443", "example.com", 443)]
    public void ParseHostAndPort_HandlesWithAndWithoutExplicitPort(string input, string expectedHost, int expectedPort)
    {
        var (host, port) = MainWindowViewModel.ParseHostAndPort(input);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Fact]
    public void ParseHostAndPort_TrailingColonWithoutDigits_TreatsWholeStringAsHost()
    {
        // "host:" 或 "host:abc" 不是合法的埠號,整串當主機名,不要硬解析出奇怪的結果。
        var (host, port) = MainWindowViewModel.ParseHostAndPort("192.168.0.120:");
        Assert.Equal("192.168.0.120:", host);
        Assert.Equal(DefaultPorts.Host, port);
    }

    // code review 抓到的真坑:裸 IPv6(多個冒號、沒加方括號)原本會被最後一個冒號切錯,
    // 例如「fe80::1」被誤切成 host="fe80:" port="1"。
    [Theory]
    [InlineData("fe80::1", "fe80::1", DefaultPorts.Host)]
    [InlineData("::1", "::1", DefaultPorts.Host)]
    [InlineData("2001:db8::1", "2001:db8::1", DefaultPorts.Host)]
    public void ParseHostAndPort_BareIpv6WithoutBrackets_TreatsWholeStringAsHost(string input, string expectedHost, int expectedPort)
    {
        var (host, port) = MainWindowViewModel.ParseHostAndPort(input);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    // RFC 3986 的寫法:IPv6 要帶埠號就得加方括號,「[fe80::1]:7443」才拆得出正確的 host/port。
    [Theory]
    [InlineData("[::1]", "::1", DefaultPorts.Host)]
    [InlineData("[::1]:7443", "::1", 7443)]
    [InlineData("[fe80::1]:9999", "fe80::1", 9999)]
    public void ParseHostAndPort_BracketedIpv6_ParsesHostAndPortCorrectly(string input, string expectedHost, int expectedPort)
    {
        var (host, port) = MainWindowViewModel.ParseHostAndPort(input);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }
}
