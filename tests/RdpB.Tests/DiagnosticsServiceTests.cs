using System.Net;
using RdpB.App.Services;
using RdpB.Core.Transport;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>計畫 §8 診斷頁的核心邏輯——驗證三步(QUIC/TCP/代理)真的分開跑,而不是像
/// ConnectivityChecker 那樣只看「自動選擇最後走哪一條」。</summary>
public class DiagnosticsServiceTests
{
    [Fact]
    public async Task RunAsync_NoProxy_SkipsProxyStepWithoutAttemptingConnection()
    {
        var cert = EphemeralCertificate.CreateSelfSigned();
        using var listener = TcpTlsTransportListener.Start(
            new IPEndPoint(IPAddress.Loopback, 0), cert, clientCertificateRequired: false);

        var acceptTask = listener.AcceptAsync();

        var results = await DiagnosticsService.RunAsync(
            "127.0.0.1", quicPort: 1 /* 沒人監聽,QUIC 這步預期失敗 */, tcpPort: listener.BoundPort, proxy: null);

        await using var server = await acceptTask;

        Assert.Equal(3, results.Count);
        Assert.Equal("透過代理", results[2].StepName);
        Assert.False(results[2].Attempted);
        Assert.Contains("沒有設定代理", results[2].Message);

        Assert.True(results[1].Attempted);
        Assert.True(results[1].Success); // TCP+TLS 直連這步真的連上了
    }
}
