using RdpB.App.Services;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>code review 抓到的真坑:原本 `HostListenerService` 只用 `_activeSession is not null`
/// 擋第二個連線,但那個欄位要等配對「成功」才會設。兩個人在同一個 ~60 秒配對視窗內一起連
/// 進來,兩邊都通得過這個檢查,最後會變成兩組 CaptureEncoder/AudioCapturer 互搶同一個螢幕/
/// 音效裝置。修法是一接受連線就先佔位(見 `HostListenerService._slotClaimed`),這裡驗證：
/// 第一個連線還卡在配對中(故意不送 Pair 訊息)的時候,第二個連線應該立刻被拒絕關閉,
/// 不會被晾在那邊等到 60 秒配對逾時。
///
/// 這個測試只碰網路層(TCP+TLS),不會真的跑到 CaptureEncoder(需要 GPU),所以不用標
/// [Trait("Category","Hardware")],可以跟其他快測試一起跑。</summary>
public class HostListenerServiceConcurrencyTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"rdpb-concurrency-test-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task SecondConnection_WhileFirstIsStillPairing_IsRejectedImmediately()
    {
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        var cert1 = EphemeralCertificate.CreateSelfSigned("integration-test-client-1");
        await using var transport1 = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: cert1);
        var session1 = new RdpB.Core.Session.Session(transport1, heartbeatInterval: TimeSpan.FromMilliseconds(200));
        session1.Start();
        // 故意不送 Pair 訊息——讓第一個連線卡在「已接受、尚未配對完成」的狀態,佔著 slot。

        var cert2 = EphemeralCertificate.CreateSelfSigned("integration-test-client-2");
        await using var transport2 = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: cert2);
        var session2 = new RdpB.Core.Session.Session(transport2, heartbeatInterval: TimeSpan.FromMilliseconds(200));

        // Session 偵測到 transport 斷線後,先進 Reconnecting(給重連機會),要等重連視窗
        // 逾時才會變 Closed——這裡只在乎「馬上被斷開」這件事本身,不用等到 Closed,
        // 看到第一次脫離 Active 的狀態轉換就足以證明 Host 立刻拒絕了這條連線。
        var disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session2.StateChanged += state =>
        {
            if (state is not RdpB.Core.Session.SessionState.Active) disconnectedTcs.TrySetResult(true);
        };
        session2.Start();

        // 修好之前:第二個連線會被晾著等完整的 60 秒配對逾時。修好之後:Host 一接受連線
        // 就先佔位檢查,發現已經被佔用,立刻關閉連線——幾秒內就該看到狀態轉換。
        var completed = await Task.WhenAny(disconnectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(disconnectedTcs.Task, completed);

        await session1.DisposeAsync();
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }
}
