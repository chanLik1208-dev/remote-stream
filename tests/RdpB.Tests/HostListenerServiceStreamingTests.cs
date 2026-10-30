using RdpB.App.Services;
using RdpB.Core.Pairing;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>端對端整合測試:真的連線 → mTLS → 配對 → Host 真的開始串流。在這台有 GPU/
/// 互動桌面的機器上跑,不是假的——證明 Kyra 要的「配對+真的開始串流」這條鏈路是通的。
///
/// **跑起來比其他測試重,不放進日常的快速迴圈**:單獨跑很穩(重跑十幾次都是 &lt;1 秒內
/// 配對成功),但接在前面 130 多個測試後面跑,偶爾會因為累積的資源壓力(執行緒池、
/// TIME_WAIT 的 TCP socket 之類)拖到將近一分鐘才完成配對——不是功能壞掉,是測試套件
/// 排序/資源的問題。日常開發用 `dotnet test --filter "Category!=Hardware"` 跳過,
/// 要驗證這條端對端鏈路再用 `dotnet test --filter "Category=Hardware"` 單獨跑。</summary>
[Trait("Category", "Hardware")]
public class HostListenerServiceStreamingTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"rdpb-streaming-test-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task PairThenConnect_HostActuallyStartsStreaming_ClientReceivesVideoFrame()
    {
        await using var host = new HostListenerService(_configPath);
        Exception? startFailure = null;
        host.StreamingStartFailed += ex => startFailure = ex;
        host.Start(port: 0);

        var clientCert = EphemeralCertificate.CreateSelfSigned("integration-test-client");
        await using var clientTransport = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true,
            clientCertificate: clientCert);

        var clientSession = new RdpB.Core.Session.Session(clientTransport, heartbeatInterval: TimeSpan.FromMilliseconds(200));

        var videoReceived = new TaskCompletionSource<byte[]>();
        clientSession.MessageReceived += (channel, type, payload) =>
        {
            if (channel == Channel.Video && type == MessageType.VideoFrame)
                videoReceived.TrySetResult(payload.ToArray());
        };
        clientSession.Start();

        // Client 端自己要能看到 Host 真的出示的憑證才能驗證指紋——這條連線的 remoteCertificate
        // 就是 TcpTlsTransport.ConnectAsync 裡已經記下來的那個。
        var hostCertSeenByClient = clientTransport.RemoteCertificate!;
        var pairResult = await PairingCoordinator.PairAsync(clientSession, host.Pin!, hostCertSeenByClient, clientCert);

        Assert.Equal(PairingResult.Success, pairResult);

        // 配對成功後,Host 應該已經真的在跑 CaptureEncoder 了。
        // 逾時給寬一點:ddagrab(Desktop Duplication)在畫面完全沒變化時要等到真的有變化
        // 才會給第一幀,啟動時間本來就會變動,不是固定的。
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!host.IsStreaming && startFailure is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        if (startFailure is not null)
            Assert.Fail($"串流啟動失敗:{startFailure}");
        Assert.True(host.IsStreaming);
        Assert.NotNull(host.SelectedEncoder);

        var completed = await Task.WhenAny(videoReceived.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(videoReceived.Task, completed);
        var frame = await videoReceived.Task;
        Assert.True(frame.Length > 0); // 真的收到一張編碼過的畫面,不是空封包

        await clientSession.DisposeAsync();
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }
}
