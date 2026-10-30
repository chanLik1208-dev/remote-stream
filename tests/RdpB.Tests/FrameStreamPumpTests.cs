using System.Net;
using System.Net.Sockets;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;

namespace RdpB.Tests;

/// <summary>
/// 直接測 FrameStreamPump 的讀寫迴圈,尤其是「payload 非空」的情況——這正是
/// QuicTransport 開發時踩到的 bug(WriteTo 誤用在只有 header 大小的緩衝區上)
/// 唯一會露出來的地方,之前的 Frame/ChannelSendQueue 單元測試都繞過了實際送出這一步。
/// </summary>
public class FrameStreamPumpTests
{
    [Fact]
    public async Task SendAndReceive_FrameWithNonEmptyPayload_RoundTrips()
    {
        var (a, b) = await CreateLoopbackPairAsync();
        try
        {
            var queueA = new ChannelSendQueue();
            var pumpA = new FrameStreamPump(a, queueA);
            var ctsA = new CancellationTokenSource();
            pumpA.Start(ctsA.Token);

            var queueB = new ChannelSendQueue();
            var pumpB = new FrameStreamPump(b, queueB);
            var ctsB = new CancellationTokenSource();

            var received = new TaskCompletionSource<(Channel, MessageType, byte[])>();
            pumpB.MessageReceived += (ch, type, payload) =>
                received.TrySetResult((ch, type, payload.ToArray()));
            pumpB.Start(ctsB.Token);

            var payload = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray(); // 故意超過 header 的 6 bytes
            queueA.Enqueue(new Frame(Channel.Video, MessageType.VideoFrame, payload));

            var (ch, type, actualPayload) = await WithTimeout(received.Task);
            Assert.Equal(Channel.Video, ch);
            Assert.Equal(MessageType.VideoFrame, type);
            Assert.Equal(payload, actualPayload);

            ctsA.Cancel();
            ctsB.Cancel();
            await pumpA.StopAsync();
            await pumpB.StopAsync();
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public async Task SendAndReceive_EmptyPayload_StillWorks()
    {
        var (a, b) = await CreateLoopbackPairAsync();
        try
        {
            var queueA = new ChannelSendQueue();
            var pumpA = new FrameStreamPump(a, queueA);
            var ctsA = new CancellationTokenSource();
            pumpA.Start(ctsA.Token);

            var queueB = new ChannelSendQueue();
            var pumpB = new FrameStreamPump(b, queueB);
            var ctsB = new CancellationTokenSource();
            var received = new TaskCompletionSource<MessageType>();
            pumpB.MessageReceived += (_, type, _) => received.TrySetResult(type);
            pumpB.Start(ctsB.Token);

            queueA.Enqueue(new Frame(Channel.Control, MessageType.Heartbeat, Array.Empty<byte>()));

            var type = await WithTimeout(received.Task);
            Assert.Equal(MessageType.Heartbeat, type);

            ctsA.Cancel();
            ctsB.Cancel();
            await pumpA.StopAsync();
            await pumpB.StopAsync();
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    private static async Task<(NetworkStream a, NetworkStream b)> CreateLoopbackPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await acceptTask;
        listener.Stop();

        return (client.GetStream(), server.GetStream());
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int seconds = 5)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.Same(task, completed);
        return await task;
    }
}
