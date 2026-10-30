using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RdpB.Core.Input;
using RdpB.Core.Protocol;

namespace RdpB.Client.Input;

/// <summary>
/// 計畫 §5 的 Client 端輸入擷取,接到 <see cref="CaptureStateMachine"/> 的決策邏輯上。
///
/// **尚待接線**:這個類別假設已經有一個串流視窗的 HWND 可以掛(<see cref="AttachToWindow"/>),
/// 但那個 D3D11 視窗本身(計畫 §4/實作順序第 3 步)還沒寫——這裡先把輸入這一側的邏輯和
/// Win32 細節準備好,等視窗做出來直接接上,不用再碰一次 Raw Input/hook 的部分。
///
/// - 滑鼠完全靠 <see cref="Native.WM_INPUT"/>(Raw Input),只在視窗前景時收(不加
///   RIDEV_INPUTSINK),符合「只有視窗在前景時才會擷取」。
/// - 鍵盤只在 <see cref="CaptureStateMachine.IsActive"/> 且視窗是前景時掛 LL hook,
///   hook proc 裡只把事件丟進 <see cref="_pending"/> 佇列就 return,不做任何 I/O
///   (避免 remote-stream 那個「hook 裡 Console.WriteLine 拖慢全系統滑鼠」的坑)。
/// - 失焦/切換控制/斷線都走 <see cref="CaptureStateMachine.Deactivate"/>,統一處理放開。
/// </summary>
public sealed class InputCaptureController : IDisposable
{
    private readonly CaptureStateMachine _state = new();
    private readonly Action<Channel, MessageType, byte[]> _send;
    private readonly ConcurrentQueue<Action> _pending = new();

    /// <summary>串流視窗自己的退出/切換控制熱鍵(例如 Ctrl+Alt+Shift+Q/C、F9)要繞過這裡的
    /// 攔截——不然 Active 時這個 hook 會把每個鍵都吞掉轉送給 Host,熱鍵自己的 RegisterHotKey
    /// 永遠收不到那個按鍵事件。呼叫端(StreamWindow)決定哪些組合算保留鍵。</summary>
    private readonly Func<Native.KBDLLHOOKSTRUCT, bool>? _isReservedCombo;

    private IntPtr _hwnd;
    private IntPtr _kbHook;
    private Native.HookProc? _kbProc; // 要保留這個 delegate 的參考,不然會被 GC 回收
    private int _clientWidth, _clientHeight;
    private int _originX, _originY; // 畫面在視窗裡的 letterbox 偏移量(螢幕座標換算用,見 ClipToClientArea)

    public CaptureStateMachine State => _state;

    public InputCaptureController(
        Action<Channel, MessageType, byte[]> send,
        Func<Native.KBDLLHOOKSTRUCT, bool>? isReservedCombo = null)
    {
        _send = send;
        _isReservedCombo = isReservedCombo;
    }

    public void AttachToWindow(IntPtr hwnd, int clientWidth, int clientHeight, int originX = 0, int originY = 0)
    {
        _hwnd = hwnd;
        _clientWidth = clientWidth;
        _clientHeight = clientHeight;
        _originX = originX;
        _originY = originY;

        var device = new Native.RAWINPUTDEVICE
        {
            usUsagePage = 0x01,
            usUsage = 0x02, // 滑鼠
            dwFlags = 0,    // 不加 RIDEV_INPUTSINK——只有前景時才收
            hwndTarget = hwnd,
        };
        Native.RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
    }

    public void OnClientSizeChanged(int width, int height, int originX = 0, int originY = 0)
    {
        _clientWidth = width;
        _clientHeight = height;
        _originX = originX;
        _originY = originY;
        if (_state.IsActive && _state.MouseMode == MouseMode.Relative)
            ClipToClientArea();
    }

    /// <summary>串流視窗的 WndProc 每個訊息都丟進來這裡處理。回傳 true 表示訊息已經處理掉,
    /// 呼叫端不用再轉給 DefWindowProc(目前全部訊息都還是會轉給 DefWindowProc,回傳值保留給
    /// 之後需要攔截的情況)。</summary>
    public void HandleWindowMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case Native.WM_KILLFOCUS:
            case Native.WM_ACTIVATE when (int)wParam == 0: // WA_INACTIVE
                Deactivate();
                break;

            case Native.WM_SIZE:
            case Native.WM_DISPLAYCHANGE:
                if (_state.IsActive && _state.MouseMode == MouseMode.Relative)
                    ClipToClientArea();
                break;

            case Native.WM_INPUT:
                if (_state.IsActive && _state.MouseMode == MouseMode.Relative)
                    HandleRawInput(lParam);
                break;

            case Native.WM_MOUSEMOVE:
                if (_state.IsActive && _state.MouseMode == MouseMode.Absolute)
                    HandleAbsoluteMouseMove(lParam);
                break;

            case Native.WM_LBUTTONDOWN: HandleButton(0, true); break;
            case Native.WM_LBUTTONUP: HandleButton(0, false); break;
            case Native.WM_RBUTTONDOWN: HandleButton(1, true); break;
            case Native.WM_RBUTTONUP: HandleButton(1, false); break;
            case Native.WM_MBUTTONDOWN: HandleButton(2, true); break;
            case Native.WM_MBUTTONUP: HandleButton(2, false); break;

            case Native.WM_MOUSEWHEEL:
                if (_state.IsActive)
                {
                    short delta = (short)(((long)wParam >> 16) & 0xffff);
                    Send(MessageType.Wheel, [(byte)(delta & 0xff), (byte)((delta >> 8) & 0xff), 0]);
                }
                break;
        }
    }

    /// <summary>切換控制熱鍵按下那一刻呼叫(呼叫端自己判斷「按下的那一刻」,不是每次 WM_KEYDOWN)。</summary>
    public void ToggleControl()
    {
        bool nowActive = _state.ToggleControl();
        if (nowActive)
        {
            InstallKeyboardHookIfNeeded();
            if (_state.MouseMode == MouseMode.Relative) ClipToClientArea();
        }
        else
        {
            Deactivate();
        }
    }

    public void ToggleMouseMode()
    {
        var mode = _state.ToggleMouseMode();
        if (_state.IsActive)
        {
            if (mode == MouseMode.Relative) ClipToClientArea();
            else Native.ClipCursorNull(IntPtr.Zero);
        }
    }

    /// <summary>失焦/斷線/視窗關閉時呼叫。對應計畫 §5——統一的放開入口。</summary>
    public void Deactivate()
    {
        var snapshot = _state.Deactivate();
        UninstallKeyboardHook();
        Native.ClipCursorNull(IntPtr.Zero);

        foreach (var scanCode in snapshot.Keys)
            Send(MessageType.Key, [(byte)(scanCode & 0xff), (byte)(scanCode >> 8), 0]); // flags=0 => up
        foreach (var button in snapshot.MouseButtons)
            Send(MessageType.MouseBtn, [button, 0]);

        if (!snapshot.IsEmpty)
            Send(MessageType.ReleaseAll, []);
    }

    private void HandleRawInput(IntPtr lParam)
    {
        uint size = (uint)Marshal.SizeOf<Native.RAWINPUT>();
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, out var raw, ref size,
                (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>()) == unchecked((uint)-1))
            return;

        if (raw.header.dwType != Native.RIM_TYPEMOUSE) return;
        if (raw.mouse.lLastX == 0 && raw.mouse.lLastY == 0) return;

        short dx = (short)Math.Clamp(raw.mouse.lLastX, short.MinValue, short.MaxValue);
        short dy = (short)Math.Clamp(raw.mouse.lLastY, short.MinValue, short.MaxValue);
        Send(MessageType.MouseRel, [(byte)(dx & 0xff), (byte)((dx >> 8) & 0xff), (byte)(dy & 0xff), (byte)((dy >> 8) & 0xff)]);
    }

    private void HandleAbsoluteMouseMove(IntPtr lParam)
    {
        int x = (short)((long)lParam & 0xffff);
        int y = (short)(((long)lParam >> 16) & 0xffff);
        if (_clientWidth <= 1 || _clientHeight <= 1) return;

        ushort nx = (ushort)Math.Clamp((long)x * 65535 / (_clientWidth - 1), 0, 65535);
        ushort ny = (ushort)Math.Clamp((long)y * 65535 / (_clientHeight - 1), 0, 65535);
        Send(MessageType.MouseAbs, [(byte)(nx & 0xff), (byte)(nx >> 8), (byte)(ny & 0xff), (byte)(ny >> 8)]);
    }

    private void HandleButton(byte button, bool down)
    {
        if (!_state.IsActive) return;
        if (down) _state.Keys.OnMouseButtonDown(button); else _state.Keys.OnMouseButtonUp(button);
        Send(MessageType.MouseBtn, [button, (byte)(down ? 1 : 0)]);
    }

    private void ClipToClientArea()
    {
        // ClipCursor 吃的是螢幕座標,不是 client-area 相對座標——用 ClientToScreen 把
        // 畫面在視窗裡的實際矩形(扣掉 letterbox 偏移,見 _originX/_originY)換算成螢幕座標。
        var topLeft = new Native.POINT { X = _originX, Y = _originY };
        var bottomRight = new Native.POINT { X = _originX + _clientWidth, Y = _originY + _clientHeight };
        Native.ClientToScreen(_hwnd, ref topLeft);
        Native.ClientToScreen(_hwnd, ref bottomRight);

        var rect = new Native.RECT { left = topLeft.X, top = topLeft.Y, right = bottomRight.X, bottom = bottomRight.Y };
        Native.ClipCursor(ref rect);
    }

    private void InstallKeyboardHookIfNeeded()
    {
        if (_kbHook != IntPtr.Zero) return;
        _kbProc = KeyboardHookProc;
        _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, Native.GetModuleHandle(null), 0);
    }

    private void UninstallKeyboardHook()
    {
        if (_kbHook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_kbHook);
        _kbHook = IntPtr.Zero;
        _kbProc = null;
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 退出/切換控制這類串流視窗自己的熱鍵(StreamWindow 用 RegisterHotKey 註冊)一定要
        // 繞過這個攔截——不然 Active 時這裡會把每個鍵都吞掉轉送給 Host,熱鍵永遠按不到,
        // 使用者會被鎖在遠端畫面裡跳不出來。檢查順序放在最前面,不管 Active/前景狀態都先看。
        if (nCode >= 0 && _isReservedCombo is not null)
        {
            var reservedCheckInfo = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (_isReservedCombo(reservedCheckInfo))
                return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // 只允許在「擷取中 且 視窗是前景」時攔截——不是前景就讓本機正常處理,
        // 這是跟 remote-stream 全域 hook 最大的差別。
        if (nCode >= 0 && _state.IsActive && Native.GetForegroundWindow() == _hwnd)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;

            if (down || up)
            {
                ushort scanCode = (ushort)info.scanCode;
                bool extended = (info.flags & 0x01) != 0;
                // hook proc 裡只排隊,不做 I/O——避免卡住全系統鍵鼠(計畫 §5 的已知坑)。
                _pending.Enqueue(() =>
                {
                    if (down) _state.Keys.OnKeyDown(scanCode); else _state.Keys.OnKeyUp(scanCode);
                    byte flags = (byte)((down ? 1 : 0) | (extended ? 2 : 0));
                    Send(MessageType.Key, [(byte)(scanCode & 0xff), (byte)(scanCode >> 8), flags]);
                });
                DrainPending();
                return 1; // 攔截本機(擷取中,不讓本機也收到這個鍵)
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>目前直接在呼叫端(視窗訊息迴圈的執行緒)同步處理佇列——之後如果發現 hook 執行緒
    /// 和訊息迴圈執行緒不同、需要真的排到別的執行緒,只要改這個方法,呼叫端不用動。</summary>
    private void DrainPending()
    {
        while (_pending.TryDequeue(out var action)) action();
    }

    private void Send(MessageType type, byte[] payload) => _send(Channel.Input, type, payload);

    public void Dispose()
    {
        UninstallKeyboardHook();
        Native.ClipCursorNull(IntPtr.Zero);
    }
}
