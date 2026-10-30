using RdpB.Client.Input;
using RdpB.Core.Protocol;

namespace RdpB.Tests;

/// <summary>
/// 測試 HandleWindowMessage 的分派邏輯——不碰 Raw Input/LL hook 實際收資料那部分
/// (那需要真的視窗收到系統送來的訊息,單元測試環境做不到,要等接上 D3D11 視窗後
/// 由 Kyra 實際操作驗證)。這裡驗證的是:該不該送、送什麼、以及失焦/切換時有沒有正確放開。
/// </summary>
public class InputCaptureControllerTests
{
    private sealed record SentMessage(Channel Channel, MessageType Type, byte[] Payload);

    private static (InputCaptureController ctrl, List<SentMessage> sent) CreateController()
    {
        var sent = new List<SentMessage>();
        var ctrl = new InputCaptureController((ch, type, payload) => sent.Add(new SentMessage(ch, type, payload)));
        return (ctrl, sent);
    }

    [Fact]
    public void MouseButton_WhenNotActive_IsIgnored()
    {
        var (ctrl, sent) = CreateController();
        ctrl.HandleWindowMessage(0x0201 /* WM_LBUTTONDOWN */, IntPtr.Zero, IntPtr.Zero);
        Assert.Empty(sent);
    }

    [Fact]
    public void MouseButton_WhenActive_SendsMouseBtnAndTracksState()
    {
        var (ctrl, sent) = CreateController();
        ctrl.ToggleControl(); // Active

        ctrl.HandleWindowMessage(0x0201 /* WM_LBUTTONDOWN */, IntPtr.Zero, IntPtr.Zero);

        Assert.Contains(sent, m => m.Type == MessageType.MouseBtn && m.Payload[0] == 0 && m.Payload[1] == 1);
        Assert.Equal(1, ctrl.State.Keys.DownMouseButtonCount);
    }

    [Fact]
    public void Deactivate_WithHeldMouseButton_SendsReleaseAndReleaseAllSignal()
    {
        var (ctrl, sent) = CreateController();
        ctrl.ToggleControl();
        ctrl.HandleWindowMessage(0x0201 /* WM_LBUTTONDOWN */, IntPtr.Zero, IntPtr.Zero);
        sent.Clear();

        ctrl.Deactivate();

        Assert.Contains(sent, m => m.Type == MessageType.MouseBtn && m.Payload[1] == 0); // 放開
        Assert.Contains(sent, m => m.Type == MessageType.ReleaseAll);
        Assert.False(ctrl.State.IsActive);
        Assert.Equal(0, ctrl.State.Keys.DownMouseButtonCount);
    }

    [Fact]
    public void Deactivate_WithNothingHeld_DoesNotSendReleaseAllSignal()
    {
        var (ctrl, sent) = CreateController();
        ctrl.ToggleControl();
        sent.Clear();

        ctrl.Deactivate();

        Assert.DoesNotContain(sent, m => m.Type == MessageType.ReleaseAll);
    }

    [Fact]
    public void WM_KILLFOCUS_DeactivatesAndReleasesHeldButton()
    {
        var (ctrl, sent) = CreateController();
        ctrl.ToggleControl();
        ctrl.HandleWindowMessage(0x0204 /* WM_RBUTTONDOWN */, IntPtr.Zero, IntPtr.Zero);
        sent.Clear();

        ctrl.HandleWindowMessage(0x0008 /* WM_KILLFOCUS */, IntPtr.Zero, IntPtr.Zero);

        Assert.False(ctrl.State.IsActive);
        Assert.Contains(sent, m => m.Type == MessageType.MouseBtn && m.Payload[0] == 1 && m.Payload[1] == 0);
    }

    [Fact]
    public void WM_ACTIVATE_Inactive_Deactivates()
    {
        var (ctrl, _) = CreateController();
        ctrl.ToggleControl();

        ctrl.HandleWindowMessage(0x0006 /* WM_ACTIVATE */, IntPtr.Zero, IntPtr.Zero); // wParam=0 => WA_INACTIVE

        Assert.False(ctrl.State.IsActive);
    }

    [Fact]
    public void MouseWheel_WhenActive_SendsWheelMessage()
    {
        var (ctrl, sent) = CreateController();
        ctrl.ToggleControl();

        // WM_MOUSEWHEEL:wParam 高 16 bits 是 delta(通常 ±120 的倍數)
        IntPtr wParam = (IntPtr)(120 << 16);
        ctrl.HandleWindowMessage(0x020A /* WM_MOUSEWHEEL */, wParam, IntPtr.Zero);

        Assert.Contains(sent, m => m.Type == MessageType.Wheel);
    }

    [Fact]
    public void AbsoluteMouseMove_WhenActive_SendsNormalizedCoordinates()
    {
        var (ctrl, sent) = CreateController();
        ctrl.OnClientSizeChanged(800, 600);
        ctrl.ToggleControl(); // 預設是 Absolute 模式

        IntPtr lParam = (IntPtr)(400 | (300 << 16)); // 畫面正中央
        ctrl.HandleWindowMessage(0x0200 /* WM_MOUSEMOVE */, IntPtr.Zero, lParam);

        var abs = sent.Single(m => m.Type == MessageType.MouseAbs);
        ushort nx = (ushort)(abs.Payload[0] | (abs.Payload[1] << 8));
        ushort ny = (ushort)(abs.Payload[2] | (abs.Payload[3] << 8));

        // 400/799 ≈ 0.5006,換成 0~65535 應該接近正中央
        Assert.InRange(nx, 32000, 33500);
        Assert.InRange(ny, 32000, 33500);
    }

    [Fact]
    public void ToggleControl_TwiceInARow_EndsInactive_WithNoHeldKeys()
    {
        var (ctrl, _) = CreateController();
        ctrl.ToggleControl();
        Assert.True(ctrl.State.IsActive);

        ctrl.ToggleControl();
        Assert.False(ctrl.State.IsActive);
        Assert.Equal(0, ctrl.State.Keys.DownKeyCount);
    }
}
