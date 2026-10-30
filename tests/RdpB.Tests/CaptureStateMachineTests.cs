using RdpB.Core.Input;

namespace RdpB.Tests;

public class CaptureStateMachineTests
{
    [Fact]
    public void ToggleControl_FlipsActiveState()
    {
        var csm = new CaptureStateMachine();
        Assert.False(csm.IsActive);

        Assert.True(csm.ToggleControl());
        Assert.True(csm.IsActive);

        Assert.False(csm.ToggleControl());
        Assert.False(csm.IsActive);
    }

    [Fact]
    public void Deactivate_WhileKeysHeld_ReturnsThemAndClearsActive()
    {
        var csm = new CaptureStateMachine();
        csm.ToggleControl();
        csm.Keys.OnKeyDown(0x11);
        csm.Keys.OnKeyDown(0x2A);

        var snapshot = csm.Deactivate();

        Assert.False(csm.IsActive);
        Assert.Contains((ushort)0x11, snapshot.Keys);
        Assert.Contains((ushort)0x2A, snapshot.Keys);
    }

    [Fact]
    public void Deactivate_CalledTwice_SecondCallReturnsEmpty()
    {
        var csm = new CaptureStateMachine();
        csm.ToggleControl();
        csm.Keys.OnKeyDown(0x11);

        csm.Deactivate();
        var second = csm.Deactivate();

        Assert.True(second.IsEmpty);
    }

    [Fact]
    public void Deactivate_WhenNotActive_IsSafeAndEmpty()
    {
        var csm = new CaptureStateMachine();
        var snapshot = csm.Deactivate();
        Assert.True(snapshot.IsEmpty);
        Assert.False(csm.IsActive);
    }

    [Fact]
    public void ToggleMouseMode_DefaultsToAbsolute_ThenFlips()
    {
        var csm = new CaptureStateMachine();
        Assert.Equal(MouseMode.Absolute, csm.MouseMode);

        Assert.Equal(MouseMode.Relative, csm.ToggleMouseMode());
        Assert.Equal(MouseMode.Absolute, csm.ToggleMouseMode());
    }

    [Fact]
    public void FocusLostScenario_WDownThenDeactivate_ReleasesW()
    {
        // 對應計畫驗證清單:「按住 W+Shift 時 Alt+Tab」這個情境在狀態機這一層的樣子。
        var csm = new CaptureStateMachine();
        csm.ToggleControl();
        csm.Keys.OnKeyDown(0x11); // W
        csm.Keys.OnKeyDown(0x2A); // Shift

        // 模擬 Alt+Tab 造成 WM_KILLFOCUS
        var released = csm.Deactivate();

        Assert.Equal(2, released.Keys.Length);
        Assert.Equal(0, csm.Keys.DownKeyCount);
    }
}
