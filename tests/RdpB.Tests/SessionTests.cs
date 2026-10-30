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
