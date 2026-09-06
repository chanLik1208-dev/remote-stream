// 手把:客戶端讀 XInput → 送 PAD 封包;主機用 ViGEmBus 建虛擬 Xbox360 手把注入
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace RemotePlay;

// XInput 讀取（客戶端本機實體手把）
static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    public struct State { public uint dwPacketNumber; public Gamepad Gamepad; }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint index, out State state);

    public static bool Get(uint index, out Gamepad g)
    {
        uint r = XInputGetState(index, out var s);
        g = s.Gamepad;
        return r == 0; // ERROR_SUCCESS
    }
}

// 客戶端：輪詢 XInput → 送 PAD 封包（~125Hz）
sealed class GamepadSender
{
    readonly UdpClient _udp;
    readonly IPEndPoint _host;
    volatile bool _run = true;
    Thread? _t;

    public GamepadSender(UdpClient udp, IPEndPoint host) { _udp = udp; _host = host; }
    public void Start() { _t = new Thread(Loop) { IsBackground = true }; _t.Start(); }
    public void Stop() { _run = false; }

    void Loop()
    {
        var buf = new byte[13];
        buf[0] = Proto.PAD;
        bool connected = false;
        while (_run)
        {
            if (XInput.Get(0, out var g))
            {
                if (!connected) { Console.WriteLine("[client] 偵測到手把，開始轉送"); connected = true; }
                buf[1] = (byte)(g.wButtons & 0xff);
                buf[2] = (byte)(g.wButtons >> 8);
                buf[3] = g.bLeftTrigger;
                buf[4] = g.bRightTrigger;
                WriteI16(buf, 5, g.sThumbLX);
                WriteI16(buf, 7, g.sThumbLY);
                WriteI16(buf, 9, g.sThumbRX);
                WriteI16(buf, 11, g.sThumbRY);
                try { _udp.Send(buf, buf.Length, _host); } catch { }
            }
            else if (connected) { connected = false; Console.WriteLine("[client] 手把已移除"); }
            Thread.Sleep(8);
        }
    }

    static void WriteI16(byte[] b, int o, short v) { b[o] = (byte)(v & 0xff); b[o + 1] = (byte)((v >> 8) & 0xff); }
}

// 主機：把 PAD 封包灌進虛擬 Xbox360 手把（需 ViGEmBus 驅動）
static class GamepadSink
{
    static ViGEmClient? _client;
    static IXbox360Controller? _pad;
    static bool _failed;

    public static void Handle(byte[] d)
    {
        if (d.Length < 13) return;
        if (_pad == null && !Ensure()) return;
        var p = _pad!;

        ushort b = (ushort)(d[1] | (d[2] << 8));
        p.SetButtonState(Xbox360Button.Up, (b & 0x0001) != 0);
        p.SetButtonState(Xbox360Button.Down, (b & 0x0002) != 0);
        p.SetButtonState(Xbox360Button.Left, (b & 0x0004) != 0);
        p.SetButtonState(Xbox360Button.Right, (b & 0x0008) != 0);
        p.SetButtonState(Xbox360Button.Start, (b & 0x0010) != 0);
        p.SetButtonState(Xbox360Button.Back, (b & 0x0020) != 0);
        p.SetButtonState(Xbox360Button.LeftThumb, (b & 0x0040) != 0);
        p.SetButtonState(Xbox360Button.RightThumb, (b & 0x0080) != 0);
        p.SetButtonState(Xbox360Button.LeftShoulder, (b & 0x0100) != 0);
        p.SetButtonState(Xbox360Button.RightShoulder, (b & 0x0200) != 0);
        p.SetButtonState(Xbox360Button.Guide, (b & 0x0400) != 0);
        p.SetButtonState(Xbox360Button.A, (b & 0x1000) != 0);
        p.SetButtonState(Xbox360Button.B, (b & 0x2000) != 0);
        p.SetButtonState(Xbox360Button.X, (b & 0x4000) != 0);
        p.SetButtonState(Xbox360Button.Y, (b & 0x8000) != 0);
        p.SetSliderValue(Xbox360Slider.LeftTrigger, d[3]);
        p.SetSliderValue(Xbox360Slider.RightTrigger, d[4]);
        p.SetAxisValue(Xbox360Axis.LeftThumbX, ReadI16(d, 5));
        p.SetAxisValue(Xbox360Axis.LeftThumbY, ReadI16(d, 7));
        p.SetAxisValue(Xbox360Axis.RightThumbX, ReadI16(d, 9));
        p.SetAxisValue(Xbox360Axis.RightThumbY, ReadI16(d, 11));
        p.SubmitReport();
    }

    static bool Ensure()
    {
        if (_failed) return false;
        try
        {
            _client = new ViGEmClient();
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
            _pad.Connect();
            Console.WriteLine("[host] 虛擬 Xbox360 手把已建立");
            return true;
        }
        catch (Exception e)
        {
            _failed = true;
            Console.WriteLine($"[host] 建立虛擬手把失敗（需安裝 ViGEmBus 驅動）：{e.Message}");
            return false;
        }
    }

    static short ReadI16(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8));
}
