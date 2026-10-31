using System.Threading;
using RdpB.Core.Protocol;
using RdpB.Core.Session;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>對應計畫驗證清單:「心跳逾時時 Host 會放開按鍵」,以及斷線→重連→放棄的狀態機。</summary>
public class SessionTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(30);

    [Fact]
    public async Task HeartbeatTimeout_TransitionsToReconnecting_AndReleasesKeys()
    {
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: Fast, heartbeatTimeout: Fast, reconnectWindow: Fast * 3);

        session.Keys.OnKeyDown(0x11); // W
        session.Keys.OnKeyDown(0x2A); // Shift

        var released = new TaskCompletionSource<ushort[]>();
        session.KeyReleaseRequested += snap => released.TrySetResult(snap.Keys);

        session.Start();
        // 不餵任何心跳回來 → 一定會逾時

        var keys = await WithTimeout(released.Task);
        Assert.Contains((ushort)0x11, keys);
        Assert.Contains((ushort)0x2A, keys);
        Assert.Equal(SessionState.Reconnecting, session.State);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Disconnected_ImmediatelyReleasesKeys_WithoutWaitingForHeartbeatTimeout()
    {
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: Fast, heartbeatTimeout: TimeSpan.FromSeconds(10), reconnectWindow: Fast * 3);
        session.Keys.OnMouseButtonDown(0);

        var released = new TaskCompletionSource<byte[]>();
        session.KeyReleaseRequested += snap => released.TrySetResult(snap.MouseButtons);

        session.Start();
        transport.SimulateDisconnect(new IOException("模擬斷線"));

        var buttons = await WithTimeout(released.Task);
        Assert.Contains((byte)0, buttons);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceivingHeartbeat_KeepsSessionActive_NeverReleases()
    {
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: Fast, heartbeatTimeout: Fast * 4, reconnectWindow: Fast * 10);

        session.Keys.OnKeyDown(0x11);
        bool released = false;
        session.KeyReleaseRequested += _ => released = true;

        session.Start();

        // 持續餵心跳,撐過原本會逾時的時間
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(Fast);
            transport.SimulateReceive(Channel.Control, MessageType.Heartbeat);
        }

        Assert.False(released);
        Assert.Equal(SessionState.Active, session.State);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Rebind_AfterReconnectWithinWindow_ReturnsToActive()
    {
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: Fast, heartbeatTimeout: Fast, reconnectWindow: Fast * 20);

        var states = new List<SessionState>();
        session.StateChanged += s => states.Add(s);

        session.Start();
        transport.SimulateDisconnect();

        // 等進入 Reconnecting
        await WaitUntil(() => session.State == SessionState.Reconnecting);

        var newTransport = new FakeTransport();
        session.Rebind(newTransport);

        Assert.Equal(SessionState.Active, session.State);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectWindowExpires_TransitionsToClosed()
    {
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: Fast, heartbeatTimeout: Fast, reconnectWindow: Fast * 2);

        session.Start();
        transport.SimulateDisconnect();

        await WaitUntil(() => session.State == SessionState.Closed, timeoutSeconds: 5);
        Assert.Equal(SessionState.Closed, session.State);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Disconnected_WhenNoKeysDown_DoesNotFireReleaseEvent()
    {
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: Fast, heartbeatTimeout: Fast, reconnectWindow: Fast * 3);

        bool fired = false;
        session.KeyReleaseRequested += _ => fired = true;

        session.Start();
        transport.SimulateDisconnect();
        await Task.Delay(Fast * 2);

        Assert.False(fired); // ReleaseAll() 在空的時候不該觸發事件(見 KeyStateTrackerTests)
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceivingPing_EchoesBackPong_WithSameTimestamp()
    {
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: TimeSpan.FromSeconds(10), heartbeatTimeout: TimeSpan.FromSeconds(10));
        session.Start();

        long ticks = DateTime.UtcNow.Ticks;
        var ping = new byte[9];
        ping[0] = 0; // ping
        BitConverter.TryWriteBytes(ping.AsSpan(1), ticks);
        transport.SimulateReceive(Channel.Control, MessageType.Heartbeat, ping);

        await WaitUntil(() => transport.Sent.Any(s => s.Type == MessageType.Heartbeat && s.Payload.Length == 9 && s.Payload[0] == 1));

        var pong = transport.Sent.First(s => s.Type == MessageType.Heartbeat && s.Payload[0] == 1);
        Assert.Equal(ticks, BitConverter.ToInt64(pong.Payload.AsSpan(1)));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReceivingPong_ComputesPositiveRtt_AndRaisesEvent()
    {
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: TimeSpan.FromSeconds(10), heartbeatTimeout: TimeSpan.FromSeconds(10));
        session.Start();

        double? measured = null;
        session.RttMeasured += rtt => measured = rtt;

        // 模擬 30ms 前送出去的 ping 現在收到回聲。
        long sentTicks = DateTime.UtcNow.AddMilliseconds(-30).Ticks;
        var pong = new byte[9];
        pong[0] = 1; // pong
        BitConverter.TryWriteBytes(pong.AsSpan(1), sentTicks);
        transport.SimulateReceive(Channel.Control, MessageType.Heartbeat, pong);

        await WaitUntil(() => measured is not null);
        Assert.InRange(measured!.Value, 20, 1000); // 寬鬆一點,重點是「正數、量到了」,不是精確計時
        Assert.Equal(measured, session.LastRttMs);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SameDisconnect_DetectedByBothHeartbeatTimeoutAndTransportEvent_OnlyCountsOnce()
    {
        // code review 抓到的真坑:心跳逾時(HeartbeatLoopAsync)跟 transport 自己的
        // Disconnected 事件,幾乎都會為同一次真正的斷線各觸發一次 EnterReconnectingOrClosed——
        // 這裡故意模擬這兩條路都被觸發(先讓心跳真的逾時,再手動送一次 SimulateDisconnect
        // 模擬 transport 事件慢一點才到),確認 ReconnectCount 只算一次,不是兩次。
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: Fast, heartbeatTimeout: Fast, reconnectWindow: Fast * 10);
        session.Start();

        await WaitUntil(() => session.State == SessionState.Reconnecting);
        Assert.Equal(1, session.ReconnectCount);

        // 模擬 transport 的 Disconnected 事件「慢一點」才為同一次斷線觸發——這是原本的
        // bug 會讓 ReconnectCount 變成 2 的那個情境。
        transport.SimulateDisconnect();
        await Task.Delay(Fast * 2);

        Assert.Equal(1, session.ReconnectCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentDisconnectSignals_TrulySimultaneous_OnlyCountsOnce()
    {
        // code review 複查第二輪抓到的坑:上一個測試(SameDisconnect_...)只驗證了「循序」
        // 觸發兩條路不會重複算,但原本的守衛是 check-then-act、沒有鎖,真正「同一瞬間」
        // 觸發才是會重複算的那個情境。這裡用 Barrier 讓一堆執行緒盡量同時衝進
        // EnterReconnectingOrClosed(透過 SimulateDisconnect 這個公開可觸發的路徑),
        // 確認 ReconnectCount 不管衝了幾次都只算一次。
        var transport = new FakeTransport();
        var session = new Session(transport, heartbeatInterval: TimeSpan.FromSeconds(10), heartbeatTimeout: TimeSpan.FromSeconds(10));
        session.Start();

        const int concurrency = 20;
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait(); // 盡量讓所有執行緒在同一瞬間一起衝進 SimulateDisconnect
            transport.SimulateDisconnect();
        })).ToArray();

        await Task.WhenAll(tasks);
        await Task.Delay(50); // 讓最後幾個事件處理跑完

        Assert.Equal(1, session.ReconnectCount);

        await session.DisposeAsync();
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int seconds = 5)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.Same(task, completed);
        return await task;
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("等待條件逾時");
            await Task.Delay(10);
        }
    }
}
