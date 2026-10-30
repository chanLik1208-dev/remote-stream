using RdpB.Core.Input;

namespace RdpB.Tests;

/// <summary>
/// 對應計畫驗證清單:「按住 W+Shift → 失去焦點 → 確認送出 release」。
/// </summary>
public class KeyStateTrackerTests
{
    private const ushort W = 0x11;
    private const ushort Shift = 0x2A;

    [Fact]
    public void ReleaseAll_ReturnsAllDownKeys_WhenFocusLost()
    {
        var tracker = new KeyStateTracker();
        tracker.OnKeyDown(W);
        tracker.OnKeyDown(Shift);
        tracker.OnMouseButtonDown(0); // 左鍵

        var snapshot = tracker.ReleaseAll();

        Assert.Contains(W, snapshot.Keys);
        Assert.Contains(Shift, snapshot.Keys);
        Assert.Contains((byte)0, snapshot.MouseButtons);
        Assert.Equal(0, tracker.DownKeyCount);
        Assert.Equal(0, tracker.DownMouseButtonCount);
    }

    [Fact]
    public void ReleaseAll_SecondCall_ReturnsEmpty_NoDoubleRelease()
    {
        var tracker = new KeyStateTracker();
        tracker.OnKeyDown(W);
        tracker.ReleaseAll();

        var second = tracker.ReleaseAll();
        Assert.True(second.IsEmpty);
    }

    [Fact]
    public void KeyUp_BeforeReleaseAll_RemovesOnlyThatKey()
    {
        var tracker = new KeyStateTracker();
        tracker.OnKeyDown(W);
        tracker.OnKeyDown(Shift);
        tracker.OnKeyUp(W);

        Assert.False(tracker.IsKeyDown(W));
        Assert.True(tracker.IsKeyDown(Shift));
        Assert.Equal(1, tracker.DownKeyCount);
    }

    [Fact]
    public void ReleaseAll_WhenNothingDown_IsEmpty()
    {
        var tracker = new KeyStateTracker();
        Assert.True(tracker.ReleaseAll().IsEmpty);
    }
}
