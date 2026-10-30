using System.Runtime.InteropServices;

namespace RdpB.Host.Input;

/// <summary>SendInput 相關的 P/Invoke。跟 remote-stream 的 `input/Program.cs` 走同一套
/// scan code 注入(遊戲相容),重寫過一遍、接到新的 KeyStateTracker 雙保險。</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public const uint INPUT_KEYBOARD = 1;
    public const uint INPUT_MOUSE = 0;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    public const uint XBUTTON1 = 0x0001;
    public const uint XBUTTON2 = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    public static void SendKey(ushort scanCode, bool down, bool extended)
    {
        uint flags = KEYEVENTF_SCANCODE | (down ? 0 : KEYEVENTF_KEYUP) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = scanCode, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void MouseAbs(ushort normalizedX, ushort normalizedY)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = normalizedX,
                    dy = normalizedY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>相對位移——不帶 ABSOLUTE,不會重置游標(計畫 §5「FPS 相對滑鼠」的關鍵)。</summary>
    public static void MouseRel(short dx, short dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void MouseBtn(byte button, bool down)
    {
        uint flags = button switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            3 or 4 => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
            _ => 0,
        };
        if (flags == 0) return;

        uint xButtonData = button == 3 ? XBUTTON1 : button == 4 ? XBUTTON2 : 0;
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = xButtonData } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void Wheel(short delta, bool horizontal)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                    mouseData = unchecked((uint)(int)delta),
                },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }
}
