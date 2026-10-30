using RdpB.Core.Protocol;
using RdpB.Core.Transport;

namespace RdpB.Tests;

public class ChannelSendQueueTests
{
    [Fact]
    public async Task Dequeue_PrefersHigherPriorityChannel()
    {
        var q = new ChannelSendQueue();
        q.Enqueue(new Frame(Channel.Video, MessageType.VideoFrame, Array.Empty<byte>()));
        q.Enqueue(new Frame(Channel.Input, MessageType.Key, Array.Empty<byte>()));

        var first = await q.DequeueAsync(CancellationToken.None);
        Assert.Equal(Channel.Input, first.Channel); // Input(1) 比 Video(3) 優先

        var second = await q.DequeueAsync(CancellationToken.None);
        Assert.Equal(Channel.Video, second.Channel);
    }

    [Fact]
    public void Enqueue_DropsOldestVideoFrame_WhenQueueDepthExceeded()
    {
        var q = new ChannelSendQueue();
        int drops = 0;
        q.FrameDropped += (ch, _) => { if (ch == Channel.Video) drops++; };

        // Video 深度門檻是 2(ChannelPolicy.QueueDepth),塞第 3 筆就該丟最舊的一筆
        q.Enqueue(new Frame(Channel.Video, MessageType.VideoFrame, new byte[] { 1 }));
        q.Enqueue(new Frame(Channel.Video, MessageType.VideoFrame, new byte[] { 2 }));
        q.Enqueue(new Frame(Channel.Video, MessageType.VideoFrame, new byte[] { 3 }));

        Assert.Equal(1, drops);
        Assert.Equal(2, q.CountInChannel(Channel.Video));
    }

    [Fact]
    public void Enqueue_NeverDropsInputOrControlChannel()
    {
        var q = new ChannelSendQueue();
        int drops = 0;
        q.FrameDropped += (_, _) => drops++;

        for (int i = 0; i < 1000; i++)
            q.Enqueue(new Frame(Channel.Input, MessageType.Key, new byte[] { (byte)i }));

        Assert.Equal(0, drops);
        Assert.Equal(1000, q.CountInChannel(Channel.Input));
    }

    [Fact]
    public async Task DequeueAsync_WaitsUntilFrameAvailable()
    {
        var q = new ChannelSendQueue();
        var dequeueTask = q.DequeueAsync(CancellationToken.None);

        Assert.False(dequeueTask.IsCompleted);

        q.Enqueue(new Frame(Channel.Control, MessageType.Heartbeat, Array.Empty<byte>()));

        var frame = await dequeueTask;
        Assert.Equal(Channel.Control, frame.Channel);
    }
}
