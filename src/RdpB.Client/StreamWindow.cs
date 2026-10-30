using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RdpB.Client.Input;
using RdpB.Client.Media;
using RdpB.Core.Protocol;
using CoreSession = RdpB.Core.Session.Session;
using Native = RdpB.Client.Input.Native;
using Win32 = RdpB.Client.Win32Window;

namespace RdpB.Client;

/// <summary>
/// 計畫 §4/§5/§8/實作順序第 3~4 步:Kyra 真的會用的串流視窗——不是丟棄的 probe。配對
/// 成功、Session 開始之後,GUI 的「連線」流程(見 RdpB.App 的 ClientConnectionService)
/// 建出一個 <see cref="CoreSession"/> 就交給這裡,剩下的畫面/音訊/輸入/視窗生命週期全部
/// 由這個類別自己管。
///
/// **用 GDI 畫面,不是計畫原本設想的 D3D11 zero-copy present**:<see cref="Decoder"/> 目前
/// 解出來的就是 CPU 端 BGRA(見那個類別上的註解),GDI 的 StretchDIBits 直接吃這個格式最
/// 省事。零拷貝那條路線留給之後的效能優化,這一版先求「畫面真的開得出來、鍵鼠真的操作得動」。
///
/// 自己開一個 Win32 視窗 + 自己的訊息迴圈執行緒(跟 Avalonia 的 UI 執行緒完全分開),這樣
/// 鍵鼠焦點才完全由我們自己的 WndProc 決定(計畫 §8:「串流畫面一律開在獨立的 Win32/D3D11
/// 視窗,不要嵌進 Avalonia」)。
///
/// **熱鍵**:切換控制 Ctrl+Alt+Shift+C、切換滑鼠模式 F9、退出 Ctrl+Alt+Shift+Q。全部用
/// RegisterHotKey(不是 WM_KEYDOWN),而且 <see cref="InputCaptureController"/> 的鍵盤 hook
/// 認得這幾個組合、Active 時也會放過不攔截——不然擷取中時這幾個鍵會被吞掉轉送給 Host,
/// 永遠按不到,人會被鎖在遠端畫面裡跳不出來。
/// </summary>
public sealed class StreamWindow : IDisposable
{
    private const string ClassName = "RdpBStreamWindow";
    private const int HotkeyToggleControl = 1, HotkeyExit = 2, HotkeyToggleMouseMode = 3;

    private static readonly object ClassLock = new();
    private static bool _classRegistered;
    private static Win32.WNDCLASSEX _wndClass; // 保留整個結構,避免 lpfnWndProc 的委派被 GC 回收
    private static Native.WndProcDelegate? _wndProcDelegate;

    private static readonly ConcurrentDictionary<IntPtr, StreamWindow> Instances = new();

    private readonly CoreSession _session;
    private readonly string _title;
    private readonly Decoder _decoder = new();
    private readonly AudioPlayer _audioPlayer = new();
    private readonly InputCaptureController _input;

    private readonly ManualResetEventSlim _windowReady = new(false);
    private Thread? _messageLoopThread;
    private IntPtr _hwnd;
    private volatile bool _closed;

    private readonly object _frameLock = new();
    private byte[]? _frameBgra;
    private int _frameWidth, _frameHeight;

    /// <summary>
    /// 真的在這台機器上炸過的坑:<see cref="Decoder.Open"/> 裡 `av_hwdevice_ctx_create` 建出來的
    /// D3D11 device 預設**不是 multithread-protected**(FFmpeg 的 D3D11VA 綁定沒有幫你呼叫
    /// `ID3D11Multithread::SetMultithreadProtected`)。Session 的訊息是在 TLS 讀取迴圈的
    /// thread pool 執行緒上到的,如果直接在那個執行緒呼叫 <see cref="Decoder.SubmitPacket"/>,
    /// 跟視窗訊息迴圈執行緒(建立 D3D11 device 的那條)不是同一條——實測會在
    /// `avcodec_receive_frame` 炸出 0xC0000005(access violation),不是偶發的測試環境問題,
    /// 是真的跨執行緒讀寫同一個未受保護的 D3D11 device。修法:把實際解碼動作搬到視窗自己的
    /// 訊息迴圈執行緒上做(跟 WM_NEWFRAME 重繪同一個模式),不要讓任何執行緒直接呼叫
    /// Decoder——Session 收到封包只是排進佇列、丟一個訊息,真正解碼永遠在同一條執行緒上發生。
    /// </summary>
    private readonly ConcurrentQueue<byte[]> _pendingVideoPackets = new();

    /// <summary>視窗關閉(使用者按退出熱鍵/叉掉視窗/連線真的斷開放棄重連)時觸發一次,
    /// 在呼叫端(App 層)的執行緒上——不是 Win32 訊息迴圈那條執行緒,呼叫端不用自己切執行緒。</summary>
    public event Action? Closed;

    public StreamWindow(CoreSession session, string title)
    {
        _session = session;
        _title = title;
        _input = new InputCaptureController(SendInput, IsReservedHotkeyCombo);
    }

    /// <summary>開視窗、接上 decoder/audio/input/session,阻塞直到視窗真的建立完成才回傳
    /// (呼叫端緊接著可能就要做別的事,不用自己猜視窗何時準備好)。CreateWindowEx 或任何一步
    /// 初始化失敗都會丟例外,不會悄悄回傳一個其實沒開起來的視窗讓呼叫端誤以為成功了。</summary>
    public void Show()
    {
        _messageLoopThread = new Thread(RunMessageLoop) { IsBackground = false, Name = "RdpB-StreamWindow" };
        _messageLoopThread.SetApartmentState(ApartmentState.STA);
        _messageLoopThread.Start();

        if (!_windowReady.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("串流視窗在 10 秒內沒有準備好。");
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"建立串流視窗失敗:{_initError}");
    }

    private string? _initError;

    private void RunMessageLoop()
    {
        EnsureClassRegistered();

        _hwnd = Win32.CreateWindowEx(
            0, ClassName, _title, Win32.WS_OVERLAPPEDWINDOW,
            Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT, 1280, 800,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _windowReady.Set();
            return;
        }

        Instances[_hwnd] = this;

        try
        {
            _decoder.FrameDecoded += OnFrameDecoded;
            _decoder.Open();

            _audioPlayer.Start();

            _input.AttachToWindow(_hwnd, 1280, 800);

            Win32.RegisterHotKey(_hwnd, HotkeyToggleControl, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, (uint)Native.VK_C);
            Win32.RegisterHotKey(_hwnd, HotkeyExit, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, (uint)Native.VK_Q);
            Win32.RegisterHotKey(_hwnd, HotkeyToggleMouseMode, Win32.MOD_NOREPEAT, (uint)Native.VK_F9);

            _session.MessageReceived += OnSessionMessageReceived;
            _session.StateChanged += OnSessionStateChanged;

            Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
            Win32.UpdateWindow(_hwnd);
        }
        catch (Exception ex)
        {
            // Decoder.Open()(找不到 ffmpeg/建立 D3D11VA device 失敗)或 AudioPlayer.Start()
            // (WASAPI 裝置問題)這類初始化失敗,不能讓視窗留著半吊子的樣子——直接摧毀掉,
            // 讓 Show() 那邊的呼叫端看到明確的例外,而不是一個開著但什麼都不會動的空視窗。
            _initError = ex.Message;
            var failedHwnd = _hwnd;
            Instances.TryRemove(failedHwnd, out _);
            _hwnd = IntPtr.Zero;
            Win32.DestroyWindow(failedHwnd);
            return;
        }
        finally
        {
            _windowReady.Set();
        }

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        CleanupResources();
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;

            _wndProcDelegate = WndProc;
            _wndClass = new Win32.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
                style = Win32.CS_HREDRAW | Win32.CS_VREDRAW,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = Native.GetModuleHandle(null),
                hbrBackground = Win32.CreateSolidBrush(0), // 全黑背景,letterbox 的部分不用額外畫
                lpszClassName = ClassName,
            };

            if (Win32.RegisterClassEx(ref _wndClass) == 0)
                throw new InvalidOperationException("RegisterClassEx 失敗,串流視窗開不起來。");

            _classRegistered = true;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Instances.TryGetValue(hwnd, out var self))
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);

        return self.HandleMessage(msg, wParam, lParam);
    }

    private IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_CLOSE:
                Win32.DestroyWindow(_hwnd);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                Instances.TryRemove(_hwnd, out _);
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;

            case Win32.WM_SIZE:
                OnResized();
                return IntPtr.Zero;

            case Win32.WM_PAINT:
                OnPaint();
                return IntPtr.Zero;

            case Win32.WM_NEWFRAME:
                Win32.InvalidateRect(_hwnd, IntPtr.Zero, false); // false:letterbox 以外的範圍只有尺寸真的變了才需要重新擦黑
                return IntPtr.Zero;

            case Win32.WM_DECODE_PENDING:
                DrainPendingVideoPackets();
                return IntPtr.Zero;

            case Win32.WM_HOTKEY:
                OnHotkey((int)wParam);
                return IntPtr.Zero;

            case Native.WM_MOUSEMOVE:
                HandleMouseMoveWithViewportOffset(lParam);
                return IntPtr.Zero;

            case Native.WM_INPUT:
            case Native.WM_KILLFOCUS:
            case Native.WM_ACTIVATE:
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_LBUTTONDOWN:
            case Native.WM_LBUTTONUP:
            case Native.WM_RBUTTONDOWN:
            case Native.WM_RBUTTONUP:
            case Native.WM_MBUTTONDOWN:
            case Native.WM_MBUTTONUP:
            case Native.WM_MOUSEWHEEL:
                _input.HandleWindowMessage(msg, wParam, lParam);
                return Native.DefWindowProc(_hwnd, msg, wParam, lParam);

            default:
                return Native.DefWindowProc(_hwnd, msg, wParam, lParam);
        }
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyToggleControl: _input.ToggleControl(); break;
            case HotkeyToggleMouseMode: _input.ToggleMouseMode(); break;
            case HotkeyExit: Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); break;
        }
    }

    /// <summary>計畫 §5「絕對模式:client-area 座標先扣掉 letterbox,再換算成主機的 0~65535」
    /// ——WM_MOUSEMOVE 給的是整個 client area 的座標,InputCaptureController 的正規化邏輯要吃
    /// 的是「畫面實際佔的那塊矩形(viewport)」相對座標,這裡先換算再轉給它,不動它的邏輯。</summary>
    private void HandleMouseMoveWithViewportOffset(IntPtr lParam)
    {
        int x = (short)((long)lParam & 0xffff);
        int y = (short)(((long)lParam >> 16) & 0xffff);

        var viewport = ComputeViewport();
        int vx = Math.Clamp(x - viewport.X, 0, Math.Max(0, viewport.Width - 1));
        int vy = Math.Clamp(y - viewport.Y, 0, Math.Max(0, viewport.Height - 1));

        var adjustedLParam = (IntPtr)((vy << 16) | (vx & 0xffff));
        _input.HandleWindowMessage(Native.WM_MOUSEMOVE, IntPtr.Zero, adjustedLParam);
    }

    private void OnResized()
    {
        Win32.GetClientRect(_hwnd, out var rect);
        var viewport = ComputeViewportFor(rect.right - rect.left, rect.bottom - rect.top);
        _input.OnClientSizeChanged(viewport.Width, viewport.Height, viewport.X, viewport.Y);
        Win32.InvalidateRect(_hwnd, IntPtr.Zero, true); // 尺寸真的變了,letterbox 範圍要重新擦黑
    }

    private readonly record struct ViewportRect(int X, int Y, int Width, int Height);

    /// <summary>把畫面等比例塞進目前的 client area,置中,多出來的部分留給視窗的黑色背景
    /// (letterbox)。沒收到畫面之前就用整個 client area 當 viewport。</summary>
    private ViewportRect ComputeViewport()
    {
        Win32.GetClientRect(_hwnd, out var rect);
        return ComputeViewportFor(rect.right - rect.left, rect.bottom - rect.top);
    }

    private ViewportRect ComputeViewportFor(int clientWidth, int clientHeight)
    {
        int frameW, frameH;
        lock (_frameLock) { frameW = _frameWidth; frameH = _frameHeight; }

        if (frameW <= 0 || frameH <= 0 || clientWidth <= 0 || clientHeight <= 0)
            return new ViewportRect(0, 0, Math.Max(clientWidth, 0), Math.Max(clientHeight, 0));

        double scale = Math.Min((double)clientWidth / frameW, (double)clientHeight / frameH);
        int destW = Math.Max(1, (int)(frameW * scale));
        int destH = Math.Max(1, (int)(frameH * scale));
        return new ViewportRect((clientWidth - destW) / 2, (clientHeight - destH) / 2, destW, destH);
    }

    private void OnPaint()
    {
        var hdc = Win32.BeginPaint(_hwnd, out var ps);
        try
        {
            byte[]? frame;
            int w, h;
            lock (_frameLock) { frame = _frameBgra; w = _frameWidth; h = _frameHeight; }
            if (frame is null) return;

            var viewport = ComputeViewport();
            Win32.SetStretchBltMode(hdc, 4 /* HALFTONE,縮放品質比預設好 */);

            var bmi = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // 負值 = top-down DIB,跟 Decoder 輸出的 BGRA 掃描順序一致,不用自己上下翻轉
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Win32.BI_RGB,
            };

            Win32.StretchDIBits(hdc, viewport.X, viewport.Y, viewport.Width, viewport.Height,
                0, 0, w, h, frame, ref bmi, Win32.DIB_RGB_COLORS, Win32.SRCCOPY);
        }
        finally
        {
            Win32.EndPaint(_hwnd, ref ps);
        }
    }

    private void OnFrameDecoded(DecodedFrame frame)
    {
        lock (_frameLock)
        {
            _frameBgra = frame.Bgra;
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
        }
        if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_NEWFRAME, IntPtr.Zero, IntPtr.Zero);
    }

    private void OnSessionMessageReceived(Channel channel, MessageType type, ReadOnlyMemory<byte> payload)
    {
        switch (channel)
        {
            case Channel.Video when type == MessageType.VideoFrame:
                // 不能在這裡直接呼叫 _decoder.SubmitPacket——這個方法是在 Session 的 TLS 讀取
                // 迴圈(thread pool 執行緒)上被呼叫的,跟建立 D3D11VA device 的視窗訊息迴圈執行緒
                // 不是同一條(見 _pendingVideoPackets 上的註解,真的在這台機器上炸過)。排進佇列,
                // 真正解碼交給視窗自己的執行緒做。
                _pendingVideoPackets.Enqueue(payload.ToArray());
                if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_DECODE_PENDING, IntPtr.Zero, IntPtr.Zero);
                break;

            case Channel.Audio when type == MessageType.AudioFrame:
                try { _audioPlayer.SubmitFrame(payload.Span); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StreamWindow] 音訊播放失敗:{ex}"); }
                break;
        }
    }

    /// <summary>永遠在視窗自己的訊息迴圈執行緒上跑——跟 Decoder.Open() 建立 D3D11VA device
    /// 的那條執行緒是同一條,見 _pendingVideoPackets 欄位上的註解。</summary>
    private void DrainPendingVideoPackets()
    {
        while (_pendingVideoPackets.TryDequeue(out var packet))
        {
            try { _decoder.SubmitPacket(packet); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StreamWindow] 解碼失敗:{ex}"); }
        }
    }

    private void OnSessionStateChanged(RdpB.Core.Session.SessionState state)
    {
        if (_hwnd == IntPtr.Zero) return;

        switch (state)
        {
            // 計畫 §8 的完整「連線中斷,正在重連…」疊字覆蓋層還沒做(那是 GUI 層的事,
            // 這裡先用標題列老實反映現況,不假裝沒事——使用者至少看得出發生了什麼)。
            case RdpB.Core.Session.SessionState.Reconnecting:
                Win32.SetWindowText(_hwnd, $"{_title}(連線中斷,正在嘗試重新連線…)");
                break;
            case RdpB.Core.Session.SessionState.Active:
                Win32.SetWindowText(_hwnd, _title);
                break;
            case RdpB.Core.Session.SessionState.Closed:
                // 重連視窗(預設 30 秒)真的放棄了才關視窗,不是一斷線就關——跟 Session 本身
                // 的語意一致,不用在這裡自己另外算一次逾時。
                Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
        }
    }

    /// <summary>StreamWindow 自己的 RegisterHotKey 組合(Ctrl+Alt+Shift+C/Q、F9)要讓
    /// InputCaptureController 的鍵盤 hook 放行、不攔截轉送——不然 Active 時這些鍵會被吞掉
    /// 送去給 Host,熱鍵的 WM_HOTKEY 永遠收不到,人會被鎖在遠端畫面裡跳不出來。</summary>
    private static bool IsReservedHotkeyCombo(Native.KBDLLHOOKSTRUCT info)
    {
        if (info.vkCode == Native.VK_F9) return true;

        bool ctrl = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
        bool alt = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
        bool shift = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
        if (!(ctrl && alt && shift)) return false;

        return info.vkCode == Native.VK_C || info.vkCode == Native.VK_Q;
    }

    private void SendInput(Channel channel, MessageType type, byte[] payload) =>
        _ = SendInputAsync(channel, type, payload);

    private async Task SendInputAsync(Channel channel, MessageType type, byte[] payload)
    {
        try { await _session.SendAsync(channel, type, payload).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch { }
    }

    private void CleanupResources()
    {
        if (_closed) return;
        _closed = true;

        _session.MessageReceived -= OnSessionMessageReceived;
        _session.StateChanged -= OnSessionStateChanged;

        Win32.UnregisterHotKey(_hwnd, HotkeyToggleControl);
        Win32.UnregisterHotKey(_hwnd, HotkeyExit);
        Win32.UnregisterHotKey(_hwnd, HotkeyToggleMouseMode);

        _input.Dispose();
        _audioPlayer.Dispose();
        _decoder.Dispose();
        _ = _session.DisposeAsync();

        Closed?.Invoke();
    }

    /// <summary>叫使用者自己按叉或退出熱鍵以外,呼叫端(App 層,例如整個程式要關掉時)也可以
    /// 直接呼叫這個強制關閉——會送 WM_CLOSE 到視窗自己的訊息迴圈執行緒,不是直接跨執行緒拆視窗。</summary>
    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
}
