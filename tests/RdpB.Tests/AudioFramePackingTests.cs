using RdpB.Core.Media;

namespace RdpB.Tests;

public class AudioFramePackingTests
{
    [Fact]
    public void PackAndUnpack_RoundTrips()
    {
        var opus = new byte[] { 1, 2, 3, 4, 5 };
        var packed = AudioFramePacking.Pack(1234, opus);

        Assert.True(AudioFramePacking.TryUnpack(packed, out var seq, out var data));
        Assert.Equal((ushort)1234, seq);
        Assert.Equal(opus, data.ToArray());
    }

    [Fact]
    public void TryUnpack_TooShort_ReturnsFalse()
    {
        Assert.False(AudioFramePacking.TryUnpack(new byte[] { 1 }, out _, out _));
    }

    [Fact]
    public void Pack_SeqWraparound_RoundTripsCorrectly()
    {
        var packed = AudioFramePacking.Pack(65535, []);
        AudioFramePacking.TryUnpack(packed, out var seq, out _);
        Assert.Equal((ushort)65535, seq);
    }
}

public class AudioSequenceTrackerTests
{
    [Fact]
    public void FirstFrame_AlwaysPlaysNormally()
    {
        var tracker = new AudioSequenceTracker();
        var decision = tracker.Observe(100);
        Assert.Equal(AudioFrameAction.PlayNormally, decision.Action);
    }

    [Fact]
    public void ConsecutiveFrames_AllPlayNormally()
    {
        var tracker = new AudioSequenceTracker();
        for (ushort i = 0; i < 20; i++)
            Assert.Equal(AudioFrameAction.PlayNormally, tracker.Observe(i).Action);
    }

    [Fact]
    public void SmallGap_ConcealsExactlyTheMissingCount()
    {
        var tracker = new AudioSequenceTracker();
        tracker.Observe(10);
        var decision = tracker.Observe(13); // 11,12 不見了

        Assert.Equal(AudioFrameAction.ConcealThenPlay, decision.Action);
        Assert.Equal(2, decision.ConcealFrameCount);
    }

    [Fact]
    public void GapLargerThanMax_ResyncsWithoutConcealing()
    {
        var tracker = new AudioSequenceTracker();
        tracker.Observe(10);
        var decision = tracker.Observe(10 + AudioSequenceTracker.MaxConcealFrames + 50);

        Assert.Equal(AudioFrameAction.PlayNormally, decision.Action);
        Assert.Equal(0, decision.ConcealFrameCount);
    }

    [Fact]
    public void GapExactlyAtMax_StillConceals()
    {
        var tracker = new AudioSequenceTracker();
        tracker.Observe(10);
        // 期望值是 11,要讓「漏掉的幀數」(diff)剛好等於 MaxConcealFrames,
        // seq 要是 11 + MaxConcealFrames,不是 10 + MaxConcealFrames。
        var decision = tracker.Observe((ushort)(11 + AudioSequenceTracker.MaxConcealFrames));

        Assert.Equal(AudioFrameAction.ConcealThenPlay, decision.Action);
        Assert.Equal(AudioSequenceTracker.MaxConcealFrames, decision.ConcealFrameCount);
    }

    [Fact]
    public void DuplicateFrame_IsDropped()
    {
        var tracker = new AudioSequenceTracker();
        tracker.Observe(10);
        tracker.Observe(11);
        var decision = tracker.Observe(11); // 同一包又來一次

        Assert.Equal(AudioFrameAction.Drop, decision.Action);
    }

    [Fact]
    public void OutOfOrderOlderFrame_IsDropped()
    {
        var tracker = new AudioSequenceTracker();
        tracker.Observe(10);
        tracker.Observe(13); // 補過 11,12
        var decision = tracker.Observe(11); // 遲到的舊封包

        Assert.Equal(AudioFrameAction.Drop, decision.Action);
    }

    [Fact]
    public void SequenceWraparound_FromMaxValueToZero_PlaysNormally()
    {
        var tracker = new AudioSequenceTracker();
        tracker.Observe(65535);
        var decision = tracker.Observe(0); // 65535 的下一個是 0(繞回)

        Assert.Equal(AudioFrameAction.PlayNormally, decision.Action);
    }

    [Fact]
    public void SequenceWraparound_WithSmallGap_StillConceals()
    {
        var tracker = new AudioSequenceTracker();
        tracker.Observe(65534);
        var decision = tracker.Observe(1); // 期望 65535,實收 1 → 繞回後差 2

        Assert.Equal(AudioFrameAction.ConcealThenPlay, decision.Action);
        Assert.Equal(2, decision.ConcealFrameCount);
    }

    [Fact]
    public void Reset_ForgetsPreviousState_TreatsNextAsFirstFrame()
    {
        var tracker = new AudioSequenceTracker();
        tracker.Observe(10);
        tracker.Reset();

        var decision = tracker.Observe(500); // 重設後,不管差多少都該是正常播放(當成第一幀)
        Assert.Equal(AudioFrameAction.PlayNormally, decision.Action);
    }
}
