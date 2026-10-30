using RdpB.Core.Protocol;

namespace RdpB.Tests;

public class FrameTests
{
    [Fact]
    public void RoundTrip_PreservesChannelTypeAndPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = new Frame(Channel.Input, MessageType.Key, payload);

        var bytes = frame.ToBytes();
        Assert.True(Frame.TryParse(bytes, out var parsed, out int consumed));

        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(Channel.Input, parsed.Channel);
        Assert.Equal(MessageType.Key, parsed.Type);
        Assert.Equal(payload, parsed.Payload.ToArray());
    }

    [Fact]
    public void TryParse_ReturnsFalse_WhenPayloadNotFullyReceived()
    {
        var frame = new Frame(Channel.Video, MessageType.VideoFrame, new byte[100]);
        var bytes = frame.ToBytes();
        var partial = bytes.AsSpan(0, Frame.HeaderSize + 50); // header 收齊,payload 只收一半

        Assert.False(Frame.TryParse(partial, out _, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_ReturnsFalse_WhenHeaderNotFullyReceived()
    {
        var bytes = new byte[] { 1, 2, 3 }; // 小於 HeaderSize
        Assert.False(Frame.TryParse(bytes, out _, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_ConsecutiveFrames_ParsesEachInTurn()
    {
        var f1 = new Frame(Channel.Control, MessageType.Heartbeat, Array.Empty<byte>());
        var f2 = new Frame(Channel.Audio, MessageType.AudioFrame, new byte[] { 9, 9 });
        var combined = f1.ToBytes().Concat(f2.ToBytes()).ToArray();

        Assert.True(Frame.TryParse(combined, out var p1, out int c1));
        Assert.Equal(MessageType.Heartbeat, p1.Type);

        Assert.True(Frame.TryParse(combined.AsSpan(c1), out var p2, out int c2));
        Assert.Equal(MessageType.AudioFrame, p2.Type);
        Assert.Equal(new byte[] { 9, 9 }, p2.Payload.ToArray());
        Assert.Equal(combined.Length, c1 + c2);
    }
}
