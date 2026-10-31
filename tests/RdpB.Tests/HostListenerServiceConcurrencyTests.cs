using RdpB.App.Services;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>這個檔案的歷史(重要,不要刪掉這段):原本這裡只有一個測試,驗證「一接受連線
/// 就先佔位」的修法——第一個連線卡在配對中時,第二個連線要立刻被拒絕關閉,不要晾著等
/// 60 秒配對逾時。
///
/// 紅隊自我攻擊測試後來抓到那個修法本身的坑更嚴重:攻擊者只要連上來什麼都不送,就能用
/// 同一招把唯一的連線槽位獨占到逾時,0 成本癱瘓所有合法連線——比猜 PIN 還便宜。
/// `HostListenerService` 因此改成「配對真的成功之後」才搶獨佔槽位(用
/// Interlocked.CompareExchange,原子操作),配對過程本身改用
/// <c>_pairingSlots</c>(SemaphoreSlim,上限 3)限制並行配對數量,不是靠獨佔槽位。
///
/// 這個檔案現在測的是新設計的兩個面向:
///  1. 並行配對上限內的連線不會互相擋(跟舊版行為相反,是刻意的)。
///  2. 超過上限的連線會被立刻拒絕(新的保護機制,防止攻擊者平行開很多條連線猜 PIN)。
/// 第三個面向(兩條連線都配對「成功」時,只有一個能真的開始串流)需要真的跑
/// CaptureEncoder(GPU),測試在 HostListenerServiceStreamingTests.cs 裡
/// (Category=Hardware)。</summary>
public class HostListenerServiceConcurrencyTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"rdpb-concurrency-test-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task MultipleConnections_WithinPairingSlotLimit_AllStayConnectedWhilePairing()
    {
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        // _pairingSlots 上限是 3——開 3 條連線,全部故意不送 Pair 訊息,確認全部都還連著
        // (沒有任何一條因為「槽位被佔用」被提早踢掉)。
        var sessions = new List<RdpB.Core.Session.Session>();
        var stillActiveFlags = new bool[3];
        for (int i = 0; i < 3; i++)
        {
            var cert = EphemeralCertificate.CreateSelfSigned($"concurrency-test-{i}");
            var transport = await TcpTlsTransport.ConnectAsync(
                "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: cert);
            var session = new RdpB.Core.Session.Session(transport, heartbeatInterval: TimeSpan.FromMilliseconds(200));
            int idx = i;
            session.StateChanged += state => stillActiveFlags[idx] = state == RdpB.Core.Session.SessionState.Active;
            session.Start();
            sessions.Add(session);
        }

        await Task.Delay(1500); // 給足夠時間讓「如果會被踢」的連線真的被踢掉

        Assert.All(stillActiveFlags, Assert.True);

        foreach (var s in sessions) await s.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionBeyondPairingSlotLimit_IsRejectedImmediately()
    {
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        // 先佔滿 3 個並行配對名額(全部不送 Pair,卡在 WaitForPairingAsync 裡)。
        var occupyingSessions = new List<RdpB.Core.Session.Session>();
        for (int i = 0; i < 3; i++)
        {
            var cert = EphemeralCertificate.CreateSelfSigned($"occupy-{i}");
            var transport = await TcpTlsTransport.ConnectAsync(
                "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: cert);
            var session = new RdpB.Core.Session.Session(transport, heartbeatInterval: TimeSpan.FromMilliseconds(200));
            session.Start();
            occupyingSessions.Add(session);
        }
        await Task.Delay(300); // 讓前 3 條真的卡進 _pairingSlots

        // 第 4 條應該立刻被拒絕(名額滿了),不用等配對逾時。
        var cert4 = EphemeralCertificate.CreateSelfSigned("overflow");
        await using var transport4 = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: cert4);
        var session4 = new RdpB.Core.Session.Session(transport4, heartbeatInterval: TimeSpan.FromMilliseconds(200));

        var disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session4.StateChanged += state =>
        {
            if (state is not RdpB.Core.Session.SessionState.Active) disconnectedTcs.TrySetResult(true);
        };
        session4.Start();

        var completed = await Task.WhenAny(disconnectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(disconnectedTcs.Task, completed);

        foreach (var s in occupyingSessions) await s.DisposeAsync();
    }

    [Fact]
    public async Task TooManyConsecutiveWrongPins_LocksOutAndRegeneratesPin()
    {
        // 紅隊測試量出來的數字:PIN 完全沒有速率限制時,單執行緒序列猜測約 100 次/秒,
        // 窮舉 100 萬種組合約 2.76 小時。這個測試驗證「連續猜錯太多次」真的會讓 Host
        // 把現在這組 PIN 作廢重發、並且進入一段鎖定期拒絕新的配對嘗試——不是只停在
        // 程式碼看起來應該這樣做。
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        string originalPin = host.Pin!;

        async Task<RdpB.Core.Pairing.PairingResult> TryWrongPinAsync()
        {
            var cert = EphemeralCertificate.CreateSelfSigned($"wrongpin-{Guid.NewGuid():N}");
            await using var transport = await TcpTlsTransport.ConnectAsync(
                "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: cert);
            var session = new RdpB.Core.Session.Session(transport, heartbeatInterval: TimeSpan.FromSeconds(10));
            session.Start();
            var result = await RdpB.Core.Pairing.PairingCoordinator.PairAsync(
                session, "000000" == originalPin ? "111111" : "000000", transport.RemoteCertificate!, cert);
            await session.DisposeAsync();
            return result;
        }

        // 連續猜錯 10 次(MaxConsecutiveWrongPin)——序列執行,每次都是一條新連線,
        // 模擬攻擊者不斷重連嘗試不同 PIN。
        //
        // 注意:PairingCoordinator.PairAsync 是「Client 端」的 API,client 收到的 PairAck
        // 只有一個成功/失敗的 bool,沒有帶失敗原因——client 端看到任何失敗都只會回
        // CertificateMismatch(這是 PairAsync 本身的設計,不是這個測試的 bug)。真正分辨
        // 「是不是 PIN 猜錯」的 WrongPin 判斷只存在 Host 端的 WaitForPairingAsync 裡,
        // HostListenerService 的鎖定計數就是用那個 Host 端的結果——這裡只需要確認「這幾次
        // 嘗試都沒有配對成功」,不需要(也沒辦法從 client 端)驗證精確的失敗原因。
        for (int i = 0; i < 10; i++)
        {
            var result = await TryWrongPinAsync();
            Assert.NotEqual(RdpB.Core.Pairing.PairingResult.Success, result);
        }

        // PIN 應該已經被作廢重發了——新的值跟原本的不一樣(理論上有極小機率隨機產生出
        // 同一組六位數,但機率是百萬分之一,測試不用處理這種極端情況)。
        Assert.NotEqual(originalPin, host.Pin);

        // 現在應該進入鎖定期——就算拿新的(正確的)PIN 來配對,也應該被直接拒絕,不會
        // 卡進正常的配對流程。
        var cert = EphemeralCertificate.CreateSelfSigned("post-lockout");
        await using var transport = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: cert);
        var session = new RdpB.Core.Session.Session(transport, heartbeatInterval: TimeSpan.FromMilliseconds(200));

        var disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += state =>
        {
            if (state is not RdpB.Core.Session.SessionState.Active) disconnectedTcs.TrySetResult(true);
        };
        session.Start();

        var completed = await Task.WhenAny(disconnectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(disconnectedTcs.Task, completed); // 鎖定期內,連線應該很快就被斷開,不是進入正常配對流程
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }
}
