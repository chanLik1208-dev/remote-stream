using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace RdpB.Host.Input;

/// <summary>Host 端:把 Client 傳來的手把狀態灌進一個虛擬 Xbox 360 手把(ViGEmBus)。
/// 跟 remote-stream 的 `Gamepad.cs` 做同一件事,搬過來接上新協定。需要先裝 ViGEmBus 驅動,
/// 沒裝的話 <see cref="Ensure"/> 會回傳 false,呼叫端自己決定要不要提示使用者去裝。</summary>
public sealed class GamepadSink : IDisposable
{
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;

    public bool Ensure()
    {
        if (_controller is not null) return true;
        try
        {
            _client = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();
            _controller.Connect();
            return true;
        }
        catch
        {
            _client?.Dispose();
            _client = null;
            _controller = null;
            return false;
        }
    }

    /// <summary>payload 格式(小端):buttons(2) + leftTrigger(1) + rightTrigger(1)
    /// + thumbLX(2) + thumbLY(2) + thumbRX(2) + thumbRY(2) = 12 bytes。</summary>
    public void Handle(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12 || !Ensure()) return;

        ushort buttons = (ushort)(payload[0] | (payload[1] << 8));
        byte leftTrigger = payload[2];
        byte rightTrigger = payload[3];
        short lx = ReadI16(payload, 4);
        short ly = ReadI16(payload, 6);
        short rx = ReadI16(payload, 8);
        short ry = ReadI16(payload, 10);

        var c = _controller!;
        c.SetButtonState(Xbox360Button.A, (buttons & 0x1000) != 0);
        c.SetButtonState(Xbox360Button.B, (buttons & 0x2000) != 0);
        c.SetButtonState(Xbox360Button.X, (buttons & 0x4000) != 0);
        c.SetButtonState(Xbox360Button.Y, (buttons & 0x8000) != 0);
        c.SetButtonState(Xbox360Button.Up, (buttons & 0x0001) != 0);
        c.SetButtonState(Xbox360Button.Down, (buttons & 0x0002) != 0);
        c.SetButtonState(Xbox360Button.Left, (buttons & 0x0004) != 0);
        c.SetButtonState(Xbox360Button.Right, (buttons & 0x0008) != 0);
        c.SetButtonState(Xbox360Button.Start, (buttons & 0x0010) != 0);
        c.SetButtonState(Xbox360Button.Back, (buttons & 0x0020) != 0);
        c.SetButtonState(Xbox360Button.LeftShoulder, (buttons & 0x0100) != 0);
        c.SetButtonState(Xbox360Button.RightShoulder, (buttons & 0x0200) != 0);
        c.SetButtonState(Xbox360Button.LeftThumb, (buttons & 0x0040) != 0);
        c.SetButtonState(Xbox360Button.RightThumb, (buttons & 0x0080) != 0);

        c.SetSliderValue(Xbox360Slider.LeftTrigger, leftTrigger);
        c.SetSliderValue(Xbox360Slider.RightTrigger, rightTrigger);
        c.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
        c.SetAxisValue(Xbox360Axis.LeftThumbY, ly);
        c.SetAxisValue(Xbox360Axis.RightThumbX, rx);
        c.SetAxisValue(Xbox360Axis.RightThumbY, ry);
    }

    private static short ReadI16(ReadOnlySpan<byte> b, int o) => (short)(b[o] | (b[o + 1] << 8));

    public void Dispose()
    {
        _controller?.Disconnect();
        _client?.Dispose();
    }
}
