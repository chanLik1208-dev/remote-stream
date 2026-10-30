using System.Net;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>
/// 對應實作順序第 2 步:QUIC transport。這台機器上 QuicListener/QuicConnection.IsSupported
/// 都是 true(用 msquic),所以可以在 loopback 上真的跑一次 handshake,不只是 mock。
/// </summary>
public class QuicTransportTests
{
    [Fact]
    public async Task ReliableChannel_DeliversMessage_ClientToServer()
    {
        await using var listener = await StartLoopbackListenerAsync();
        var acceptTask = listener.AcceptAsync();

        await using var client = await QuicTransport.ConnectAsync(
            "127.0.0.1", listener.BoundPort, _ => true);
        await using var server = await acceptTask;

        var received = new TaskCompletionSource<(Channel, MessageType, byte[])>();
        server.MessageReceived += (ch, type, payload) =>
            received.TrySetResult((ch, type, payload.ToArray()));

        await client.SendAsync(Channel.Control, MessageType.Heartbeat, new byte[] { 42 });

        var (ch, type, payload) = await WithTimeout(received.Task);
        Assert.Equal(Channel.Control, ch);
        Assert.Equal(MessageType.Heartbeat, type);
        Assert.Equal(new byte[] { 42 }, payload);
    }

    [Fact]
    public async Task ReliableChannel_IsBidirectional_ServerCanSendBackOnSameStream()
    {
        await using var listener = await StartLoopbackListenerAsync();
        var acceptTask = listener.AcceptAsync();

        await using var client = await QuicTransport.ConnectAsync("127.0.0.1", listener.BoundPort, _ => true);
        await using var server = await acceptTask;

        var received = new TaskCompletionSource<byte[]>();
        client.MessageReceived += (_, _, payload) => received.TrySetResult(payload.ToArray());

        await server.SendAsync(Channel.Input, MessageType.Key, new byte[] { 7, 7 });

        var payload = await WithTimeout(received.Task);
        Assert.Equal(new byte[] { 7, 7 }, payload);
    }

    [Fact]
    public async Task DroppableChannel_DeliversSingleVideoFrame_OverUnidirectionalStream()
    {
        await using var listener = await StartLoopbackListenerAsync();
        var acceptTask = listener.AcceptAsync();

        await using var client = await QuicTransport.ConnectAsync("127.0.0.1", listener.BoundPort, _ => true);
        await using var server = await acceptTask;

        var received = new TaskCompletionSource<(Channel, byte[])>();
        server.MessageReceived += (ch, type, payload) =>
        {
            if (type == MessageType.VideoFrame)
                received.TrySetResult((ch, payload.ToArray()));
        };

        var fakeFrame = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        await client.SendAsync(Channel.Video, MessageType.VideoFrame, fakeFrame);

        var (ch, payload) = await WithTimeout(received.Task);
        Assert.Equal(Channel.Video, ch);
        Assert.Equal(fakeFrame, payload);
    }

    [Fact]
    public async Task MultipleChannels_DoNotInterfereWithEachOther()
    {
        await using var listener = await StartLoopbackListenerAsync();
        var acceptTask = listener.AcceptAsync();

        await using var client = await QuicTransport.ConnectAsync("127.0.0.1", listener.BoundPort, _ => true);
        await using var server = await acceptTask;

        var receivedTypes = new List<MessageType>();
        var allReceived = new TaskCompletionSource();
        var sync = new object();

        server.MessageReceived += (_, type, _) =>
        {
            lock (sync)
            {
                receivedTypes.Add(type);
                if (receivedTypes.Count == 3) allReceived.TrySetResult();
            }
        };

        await client.SendAsync(Channel.Input, MessageType.Key, new byte[] { 1 });
        await client.SendAsync(Channel.Video, MessageType.VideoFrame, new byte[] { 2 });
        await client.SendAsync(Channel.Audio, MessageType.AudioFrame, new byte[] { 3 });

        await WithTimeout(allReceived.Task);
        Assert.Equal(3, receivedTypes.Count);
        Assert.Contains(MessageType.Key, receivedTypes);
        Assert.Contains(MessageType.VideoFrame, receivedTypes);
        Assert.Contains(MessageType.AudioFrame, receivedTypes);
    }

    private static async Task<QuicTransportListener> StartLoopbackListenerAsync()
    {
        var cert = EphemeralCertificate.CreateSelfSigned();
        return await QuicTransportListener.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), cert);
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int seconds = 5)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.Same(task, completed);
        return await task;
    }

    private static async Task WithTimeout(Task task, int seconds = 5)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.Same(task, completed);
        await task;
    }
}
