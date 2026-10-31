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

    [Fact]
    public async Task StopAsync_WhileStreaming_DoesNotFireStateChangedRedundantly()
    {
        // code review 複查時抓到的坑:StopAsync() 先 await _activeSession.DisposeAsync()
        // (觸發 Session.StateChanged(Closed) → 間接呼叫一次 StopStreaming()),接著自己
        // 又無條件呼叫一次 StopStreaming()——沒有狀態守衛的話,同一次停止會多出一次白白的
        // StateChanged 通知。這裡真的跑一次完整串流再停止,數 StateChanged 總共觸發幾次,
        // 確認不是「事件路徑一次 + explicit 呼叫一次 + 最後收尾一次」= 3 次,而是 2 次
        // (事件路徑那次 + 收尾那次,explicit 呼叫因為已經清過了被守衛擋下來)。
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        var clientCert = EphemeralCertificate.CreateSelfSigned("integration-test-client-2");
        await using var clientTransport = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true,
            clientCertificate: clientCert);

        var clientSession = new RdpB.Core.Session.Session(clientTransport, heartbeatInterval: TimeSpan.FromMilliseconds(200));
        clientSession.Start();

        var hostCertSeenByClient = clientTransport.RemoteCertificate!;
        var pairResult = await PairingCoordinator.PairAsync(clientSession, host.Pin!, hostCertSeenByClient, clientCert);
        Assert.Equal(PairingResult.Success, pairResult);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!host.IsStreaming && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(host.IsStreaming);

        int stateChangedCount = 0;
        host.StateChanged += () => Interlocked.Increment(ref stateChangedCount);

        await host.StopAsync();

        Assert.Equal(2, stateChangedCount);

        await clientSession.DisposeAsync();
    }

    [Fact]
    public async Task TwoConnections_BothPairSuccessfully_OnlyOneStartsStreaming()
    {
        // 這是原本 HostListenerServiceConcurrencyTests 那個坑真正要防的情境,換到新設計
        // (搶獨佔槽位的時機是「配對成功後」,不是「接受連線時」)下重新驗證一次:兩個
        // 合法使用者(都知道正確的 PIN)幾乎同時配對成功,只能有一個真的開始串流,不能
        // 兩組 CaptureEncoder/AudioCapturer 同時搶同一組硬體資源。
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        async Task<(PairingResult Pair, RdpB.Core.Session.Session Session)> ConnectAndPairAsync(string name)
        {
            var cert = EphemeralCertificate.CreateSelfSigned(name);
            var transport = await TcpTlsTransport.ConnectAsync(
                "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: cert);
            var session = new RdpB.Core.Session.Session(transport, heartbeatInterval: TimeSpan.FromMilliseconds(200));
            session.Start();
            var result = await PairingCoordinator.PairAsync(session, host.Pin!, transport.RemoteCertificate!, cert);
            return (result, session);
        }

        // 幾乎同時發起兩邊的配對,盡量逼出原本那個真坑的競態窗口。
        var task1 = ConnectAndPairAsync("race-client-1");
        var task2 = ConnectAndPairAsync("race-client-2");
        var (result1, session1) = await task1;
        var (result2, session2) = await task2;

        // 兩邊都應該配對「成功」(PIN 配對本身跟搶串流槽位是兩回事,配對不該因為對方也在
        // 配對就失敗)——真正的序列化發生在配對成功之後搶 _slotClaimed 那一步。
        Assert.Equal(PairingResult.Success, result1);
        Assert.Equal(PairingResult.Success, result2);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!host.IsStreaming && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(host.IsStreaming);

        // 給輸的那一邊一點時間被斷開(它的 session 應該離開 Active,因為 Host 在它配對成功
        // 後發現槽位已經被贏的那邊搶走,直接把它斷開)。
        await Task.Delay(1000);

        bool session1Active = session1.State == RdpB.Core.Session.SessionState.Active;
        bool session2Active = session2.State == RdpB.Core.Session.SessionState.Active;

        // 剛好只有一邊還是 Active(贏的那邊),另一邊應該已經不是 Active 了——不是兩邊都在串流。
        Assert.True(session1Active ^ session2Active, "應該剛好一邊還在串流、另一邊被斷開,不是兩邊都活著或兩邊都死了");

        await session1.DisposeAsync();
        await session2.DisposeAsync();
    }

    [Fact]
    public async Task RevokeTrustedClient_WhileStreaming_DisconnectsImmediately()
    {
        // 補 TrustedClientRevocationTests 那兩個快測試沒辦法測的那一半:撤銷「當下正在
        // 串流的那個人」要立刻生效,不能只改 config.json 讓他下次連線才被擋——那樣私鑰
        // 外洩的攻擊者會繼續操控到他自己斷線為止,撤銷等於沒用。
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        var clientCert = EphemeralCertificate.CreateSelfSigned("revoke-while-streaming");
        var fingerprint = Convert.ToHexString(PairingService.ComputeFingerprint(clientCert.RawData));
        var transport = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: clientCert);
        var session = new RdpB.Core.Session.Session(transport, heartbeatInterval: TimeSpan.FromMilliseconds(200));

        var disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += state =>
        {
            if (state is not RdpB.Core.Session.SessionState.Active) disconnectedTcs.TrySetResult(true);
        };
        session.Start();

        var pairResult = await PairingCoordinator.PairAsync(session, host.Pin!, transport.RemoteCertificate!, clientCert);
        Assert.Equal(PairingResult.Success, pairResult);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!host.IsStreaming && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(host.IsStreaming);

        host.RevokeTrustedClient(fingerprint);

        var completed = await Task.WhenAny(disconnectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(disconnectedTcs.Task, completed); // 撤銷要立刻斷線,不是卡著等它自己斷

        var afterRevokeDeadline = DateTime.UtcNow.AddSeconds(5);
        while (host.IsStreaming && DateTime.UtcNow < afterRevokeDeadline)
            await Task.Delay(50);
        Assert.False(host.IsStreaming); // Host 端的串流狀態也該跟著收掉,不是只有 Client 端斷線

        await session.DisposeAsync();
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }
}
