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
    private const int HotkeyToggleControl = 1, HotkeyExit = 2, HotkeyToggleMouseMode = 3, HotkeyToggleHud = 4;

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

    /// <summary>計畫 §9/§5「手把」——Host 端的注入早就做完了,這是補上的 Client 端讀取/
    /// 送出那一半(見 GamepadSender 類別上的註解)。跟鍵鼠共用同一個
    /// InputCaptureController.State.IsActive 當「要不要送」的判斷,行為一致。</summary>
    private readonly GamepadSender _gamepad;

    // ── Kyra 要求的隱私防護:「就算 Client 機器被裝了惡意軟體/有人在遠端監看,串流畫面
    // 本身也抓不到」。兩層都是可選的(見 PrivacySettings 上的註解),預設關閉。
    private readonly bool _blockScreenCapture;
    private readonly bool _detectTampering;
    private AntiTamperMonitor? _antiTamper;
    private volatile string? _tamperReason;

    private readonly ManualResetEventSlim _windowReady = new(false);
    private Thread? _messageLoopThread;
    private IntPtr _hwnd;
    private volatile bool _closed;

    private readonly object _frameLock = new();
    private byte[]? _frameBgra;
    private int _frameWidth, _frameHeight;

    /// <summary>計畫 §8 的「連線中斷,正在重連…」疊字覆蓋層。畫面(<see cref="_frameBgra"/>)
    /// 從來不會因為斷線被清掉——OnSessionMessageReceived 停止進新畫面,舊的那張就一直留在
    /// _frameBgra 裡,OnPaint 本來就會繼續畫它,所以「保留最後一幀」這部分本來就是現有行為,
    /// 這裡只需要再疊一塊文字 banner 上去。</summary>
    private volatile bool _isReconnecting;
    private string? _byeReason; // Host 主動斷線附帶的原因,見 WM_BYE_RECEIVED 處理

    /// <summary>Kyra 回饋的真坑:控制熱鍵(Ctrl+Alt+Shift+C)完全沒有畫面上的說明,使用者
    /// 開了串流視窗滑鼠鍵盤卻沒反應,看起來像「控制不了」的 bug。開窗後頭幾秒疊一條熱鍵
    /// 說明 banner,跟著畫面自然重繪(每收到一幀都會 InvalidateRect)自動淡出,不用另外
    /// 寫一個 Timer 去關它。</summary>
    private readonly DateTime _hotkeyHintUntil = DateTime.UtcNow.AddSeconds(8);

    /// <summary>計畫 §4/§8 的 HUD。**這一版做了「連線狀況」和「協議/編解碼細節」兩塊**,
    /// 外加位置/透明度(真的用 AlphaBlend,不是 solid 色塊)/字級/更新頻率可調、F10 在
    /// 「全部/只連線狀況/全關」三態循環、跟設定頁勾選同步。「拆解延遲」(擷取/編碼/網路/
    /// 解碼/present 五段分開顯示)還沒做——那需要在 Host 擷取/編碼熱路徑上加時間戳記並且
    /// 透過訊息傳回 Client,影響範圍比純顯示層大得多,老實標出來不是忘記。
    ///
    /// **HUD 設定用基本型別傳進來,不是整個 HudSettings 物件**——那個型別定義在
    /// RdpB.Core.Windows(跟 ConfigStore 放一起),RdpB.Client 這個專案刻意只參照
    /// RdpB.Core(platform-neutral,給之後的 Mac 版保留),不參照 RdpB.Core.Windows,
    /// 跟 blockScreenCapture/detectTampering(PrivacySettings)同一個既有模式一致。
    ///
    /// 這裡存的連線資訊是建構時呼叫端(ClientConnectionService)告訴我們的快照,連線中不會變
    /// (重連後走哪種 transport 理論上可能換,但 v1 沒有追蹤這個,只顯示最初建立時的狀態)。</summary>
    private enum HudState { All, ConnectionOnly, Off }
    private volatile HudState _hudState;
    private readonly string _hudPosition;
    private readonly double _hudOpacity;
    private readonly int _hudFontSize;
    private readonly double _hudUpdateHz;
    private readonly string _transportKind;
    private readonly bool _usedProxy;
    private readonly bool _usedRdpCamouflage;
    // 不是 volatile double(C# 不允許),但這裡只是給 HUD 顯示用的近似值,
    // 短暫的 stale read 完全無害,不需要用 Interlocked 包 bit pattern 換原子性。
    private double _lastRttMs = -1;

    /// <summary>§8「協議/編解碼細節」區塊——Encoder/目標 fps/位元率來自 Host 送的
    /// <see cref="HostStreamInfo"/>(見 OnSessionMessageReceived 裡 Channel.Control +
    /// MessageType.ConfigSync 的處理),解碼器種類/實際畫面解析度是 Client 自己量的。</summary>
    private volatile HostStreamInfo? _hostStreamInfo;
    private int _framesDecodedSinceLastTick;
    private double _actualFps;
    private System.Threading.Timer? _hudTickTimer;
    private IntPtr _hudFont; // HudSettings.FontSize 建一次、整個視窗生命週期沿用,不用每次 OnPaint 都重建

    // ── OnPaint 離屏合成緩衝區(Kyra 回報「壞 fps 時 HUD 閃爍」,見 Win32Window.BitBlt 上的
    // 註解)──────────────────────────────────────────────────────────────
    // 跟 client area 一樣大小,只在尺寸真的變了才重建(不是每次 OnPaint 都重建,那樣失去
    // 緩衝的意義,而且 CreateCompatibleDC/CreateDIBSection 本身不便宜)。
    private IntPtr _backBufferDC = IntPtr.Zero;
    private IntPtr _backBufferBitmap = IntPtr.Zero;
    private IntPtr _backBufferOldBitmap = IntPtr.Zero;
    private int _backBufferWidth, _backBufferHeight;

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

    public StreamWindow(CoreSession session, string title,
        string transportKind = "未知", bool usedProxy = false, bool usedRdpCamouflage = false,
        bool blockScreenCapture = false, bool detectTampering = false,
        string hudPreset = "connection-only", string hudPosition = "top-left",
        double hudOpacity = 0.85, int hudFontSize = 12, double hudUpdateHz = 1.0)
    {
        _session = session;
        _title = title;
        _transportKind = transportKind;
        _usedProxy = usedProxy;
        _usedRdpCamouflage = usedRdpCamouflage;
        _blockScreenCapture = blockScreenCapture;
        _detectTampering = detectTampering;
        _hudPosition = hudPosition;
        _hudOpacity = hudOpacity;
        _hudFontSize = hudFontSize;
        _hudUpdateHz = hudUpdateHz;
        _hudState = hudPreset switch
        {
            "all" => HudState.All,
            "off" => HudState.Off,
            _ => HudState.ConnectionOnly,
        };
        _input = new InputCaptureController(SendInput, IsReservedHotkeyCombo);
        _gamepad = new GamepadSender(SendInput, () => _input.State.IsActive);
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
            _gamepad.Start();

            Win32.RegisterHotKey(_hwnd, HotkeyToggleControl, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, (uint)Native.VK_C);
            Win32.RegisterHotKey(_hwnd, HotkeyExit, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, (uint)Native.VK_Q);
            Win32.RegisterHotKey(_hwnd, HotkeyToggleMouseMode, Win32.MOD_NOREPEAT, (uint)Native.VK_F9);
            Win32.RegisterHotKey(_hwnd, HotkeyToggleHud, Win32.MOD_NOREPEAT, (uint)Native.VK_F10);

            _session.MessageReceived += OnSessionMessageReceived;
            _session.StateChanged += OnSessionStateChanged;
            _session.RttMeasured += OnRttMeasured;

            Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
            Win32.UpdateWindow(_hwnd);

            if (_blockScreenCapture)
            {
                // Windows 10 2004(build 19041)之前這支 API 不存在/回傳 false——不當成致命
                // 錯誤(視窗一樣能用,只是這層保護沒生效),只留 log,不要讓舊版 Windows 上的
                // 使用者直接開不起串流視窗。
                if (!Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
                    RdpB.Core.Diagnostics.AppLog.WriteLine("[StreamWindow] SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) 失敗,可能是 Windows 版本太舊——串流視窗畫面仍會被擷取工具抓到。");
            }

            if (_detectTampering)
            {
                _antiTamper = new AntiTamperMonitor();
                _antiTamper.TamperingDetected += OnTamperingDetected;
                _antiTamper.Start();
            }

            int fontHeight = -Math.Clamp(_hudFontSize, 8, 32); // 負值 = 以字元高度(不含內距)指定,GDI CreateFont 的慣例
            _hudFont = Win32.CreateFont(fontHeight, 0, 0, 0, 400 /* FW_NORMAL */,
                0, 0, 0, Win32.DEFAULT_CHARSET, Win32.OUT_DEFAULT_PRECIS, Win32.CLIP_DEFAULT_PRECIS,
                Win32.DEFAULT_QUALITY, Win32.DEFAULT_PITCH | Win32.FF_DONTCARE, "Segoe UI");

            // HUD 整體重繪頻率(HudSettings.UpdateHz)——同一個 Timer 也順便拿來結算
            // 上一輪的「實際 fps」(DrawCodecHud 要顯示的那個數字,不是 Host 端的目標 fps)。
            // Timer 的 callback 在背景執行緒上跑,一樣不能直接碰視窗,透過 PostMessage 轉送,
            // 跟 OnTamperingDetected/AntiTamperMonitor 同一個模式。
            double hz = Math.Clamp(_hudUpdateHz, 0.1, 10.0);
            var interval = TimeSpan.FromSeconds(1.0 / hz);
            // code review 教訓(GamepadSender 真的在 Kyra 機器上炸出來的坑:Timer callback
            // 裡沒接住的例外會讓整個行程閃退,不是只斷這個功能)——這裡的 PostMessage 本身
            // 風險很低,但保險起見一樣包起來,不要讓這整類問題只修一半。
            _hudTickTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_HUD_TICK, IntPtr.Zero, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    RdpB.Core.Diagnostics.AppLog.WriteLine($"[StreamWindow] HUD tick timer 例外:{ex}");
                }
            }, null, interval, interval);
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

            case Win32.WM_TAMPERING_DETECTED:
                HandleTamperingOnWindowThread();
                return IntPtr.Zero;

            case Win32.WM_BYE_RECEIVED:
                // code review 抓到的真坑:MessageBox 如果用 _hwnd 當擁有者、又在這個視窗自己的
                // WndProc 裡同步呼叫,MessageBox 內部那個巢狀訊息迴圈還是會繼續幫這條執行緒
                // 把排隊中的訊息(包括別人這時候送來的 WM_CLOSE)繼續處理掉——如果使用者還沒
                // 關掉這個對話框,Session 就真的斷線觸發 WM_CLOSE,會在對話框還開著的時候
                // 呼叫 DestroyWindow(_hwnd),變成視窗被摧毀了但對話框還開著的未定義狀態。
                // 改成開一條完全獨立的背景執行緒彈窗(擁有者傳 IntPtr.Zero,不是 _hwnd)——
                // 只會卡住那條新執行緒,視窗自己的訊息迴圈完全不受影響,WM_CLOSE 該怎麼處理
                // 就怎麼處理,不會跟彈窗搶。
                if (_byeReason is { Length: > 0 } reason)
                {
                    var caption = $"{_title} — Host 已關閉連線";
                    new Thread(() => Win32.MessageBox(IntPtr.Zero, reason, caption, Win32.MB_OK | Win32.MB_ICONWARNING))
                    { IsBackground = true }.Start();
                }
                return IntPtr.Zero;

            case Win32.WM_HUD_TICK:
                OnHudTick();
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
            case HotkeyToggleControl:
                // Kyra 回報過一次「只按 Ctrl+C 控制就啟用了」,但 RegisterHotKey 註冊時
                // 四個修飾鍵都有給(見 Start() 附近),程式碼邏輯上找不到能繞過去的路徑——
                // 懷疑是 Windows「相黏鍵」(Sticky Keys)把 Alt/Shift 鎖在邏輯上仍按著的狀態,
                // 不是這裡的 bug。先把當下 GetAsyncKeyState 量到的修飾鍵狀態記下來,下次
                // 再發生時直接看 log 就能確認,不用用猜的。
                bool altHeld = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
                bool shiftHeld = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
                bool ctrlHeld = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
                RdpB.Core.Diagnostics.AppLog.WriteLine(
                    $"[StreamWindow] 控制熱鍵觸發——GetAsyncKeyState 量到 Ctrl={ctrlHeld} Alt={altHeld} Shift={shiftHeld}(如果 Alt/Shift 是 false,代表真的是純 Ctrl+C 觸發,不是相黏鍵)");
                _input.ToggleControl();
                break;
            case HotkeyToggleMouseMode: _input.ToggleMouseMode(); break;
            case HotkeyExit: Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); break;
            case HotkeyToggleHud:
                // 三態循環(計畫 §8):全部 → 只連線狀況 → 全關 → 回到全部。
                _hudState = _hudState switch
                {
                    HudState.All => HudState.ConnectionOnly,
                    HudState.ConnectionOnly => HudState.Off,
                    _ => HudState.All,
                };
                Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
                break;
        }
    }

    /// <summary>在 AntiTamperMonitor 的背景 Timer 執行緒上被呼叫——不能在這裡直接碰視窗/
    /// Session(跟 _pendingVideoPackets 上的註解同一個道理,任何跨執行緒直接操作 Win32 視窗
    /// 都是危險的),只負責記下原因、丟一個訊息轉送到視窗自己的執行緒。</summary>
    private void OnTamperingDetected(string reason)
    {
        _tamperReason = reason;
        if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_TAMPERING_DETECTED, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>真的在這裡執行斷線——刻意選擇「偵測到就直接斷,不是警告但繼續」:一旦懷疑
    /// 這個程序被注入了什麼東西,繼續把解碼後的畫面往下送等於讓攻擊者繼續有機會把內容挖
    /// 出去,跟保護畫面內容這個目的互相矛盾。<see cref="PrivacySettings.DetectTampering"/>
    /// 上已經講清楚這是 best-effort、會有誤判風險,使用者自己選擇要不要承擔。</summary>
    private void HandleTamperingOnWindowThread()
    {
        RdpB.Core.Diagnostics.AppLog.WriteLine($"[StreamWindow] 偵測到可能的竄改,主動中斷連線:{_tamperReason}");
        Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private void OnRttMeasured(double rttMs)
    {
        _lastRttMs = rttMs;
        // 計畫 §8 HUD「連線狀況」建議更新到 4Hz——心跳頻率(預設 500ms=2Hz)已經差不多,
        // 量到新值就直接補畫一次,不用等 HudSettings.UpdateHz 那個整體 Timer 的下一輪,
        // 畫面上的數字才會跟得上(這就是計畫原文「RTT 可以單獨設快一點」的簡化版實作——
        // 不是獨立可調的頻率,但至少 RTT 本身量到新值不會被整體更新頻率拖慢)。
        if (_hwnd != IntPtr.Zero && _hudState != HudState.Off) Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    /// <summary>HudSettings.UpdateHz 這個 Timer 到點時做的事:結算上一輪的實際 fps、
    /// 補畫一次(涵蓋「協議/編解碼細節」區塊這種沒有事件觸發、純靠定時更新的數字)。</summary>
    private void OnHudTick()
    {
        int frames = Interlocked.Exchange(ref _framesDecodedSinceLastTick, 0);
        double hz = Math.Clamp(_hudUpdateHz, 0.1, 10.0);
        _actualFps = frames * hz;
        if (_hudState != HudState.Off) Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
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

    /// <summary>Kyra 回報的真坑:壞 fps(或網路不穩、更新不規律)時 HUD 會閃爍——原本這裡
    /// 直接把影像幀跟 HUD 文字一筆一筆畫在真正的視窗 DC 上,使用者在螢幕上看到的是這些 GDI
    /// 呼叫逐步疊上去的過程(StretchDIBits 先畫好影像、DrawHudBlocks 才開始疊 HUD 的那個
    /// 空檔),畫面更新頻率低、不規律時這個「正在畫」的中間狀態被看到的機率變高。改成離屏
    /// 合成:全部畫進 <see cref="_backBufferDC"/>,畫完才用一次 <see cref="Win32.BitBlt"/>
    /// 搬到真正的視窗 DC——使用者只會看到最終完成品,看不到中間過程,HUD 不會再跟著閃。</summary>
    private void OnPaint()
    {
        var hdc = Win32.BeginPaint(_hwnd, out var ps);
        try
        {
            Win32.GetClientRect(_hwnd, out var client);
            int cw = client.right - client.left, ch = client.bottom - client.top;
            if (cw <= 0 || ch <= 0) return;

            EnsureBackBuffer(cw, ch);
            var memDC = _backBufferDC;

            // letterbox 底色——以前靠 InvalidateRect(erase:true) 讓系統在真正的視窗 DC 上擦黑,
            // 現在畫在離屏緩衝區,要自己擦一次(緩衝區重建時的那次之外,畫面尺寸不變但視角
            // 比例變了、或根本還沒收到第一幀時,也要蓋掉上一輪殘留的內容)。
            var blackBrush = Win32.CreateSolidBrush(0x00000000);
            var fullRect = new Native.RECT { left = 0, top = 0, right = cw, bottom = ch };
            Win32.FillRect(memDC, ref fullRect, blackBrush);
            Win32.DeleteObject(blackBrush);

            byte[]? frame;
            int w, h;
            lock (_frameLock) { frame = _frameBgra; w = _frameWidth; h = _frameHeight; }

            if (frame is not null)
            {
                var viewport = ComputeViewport();
                Win32.SetStretchBltMode(memDC, 4 /* HALFTONE,縮放品質比預設好 */);

                var bmi = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // 負值 = top-down DIB,跟 Decoder 輸出的 BGRA 掃描順序一致,不用自己上下翻轉
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32.BI_RGB,
                };

                Win32.StretchDIBits(memDC, viewport.X, viewport.Y, viewport.Width, viewport.Height,
                    0, 0, w, h, frame, ref bmi, Win32.DIB_RGB_COLORS, Win32.SRCCOPY);
            }
            // frame 還是 null(例如串流剛開、第一幀還沒解出來)也要繼續往下畫 HUD——
            // 不能整個 return,不然 HUD 在畫面還沒來之前完全不會顯示,一樣會被當成「閃爍/
            // 時有時無」。

            if (_hudState != HudState.Off) DrawHudBlocks(memDC);
            if (_isReconnecting) DrawReconnectingBanner(memDC);
            // code review 抓到的真坑:這條 banner 原本固定畫在頂部,跟預設就是 top-left 的
            // HUD(見 ConfigStore 的預設值)疊在一起,剛加的提示反而把使用者原本要看的連線
            // 狀況蓋住。跟 HUD 共用同一份位置設定,HUD 在上半部就改畫在下半部,避開同一塊區域。
            if (DateTime.UtcNow < _hotkeyHintUntil)
                DrawHotkeyHintBanner(memDC, atTop: _hudState == HudState.Off || _hudPosition is "bottom-left" or "bottom-right");

            Win32.BitBlt(hdc, 0, 0, cw, ch, memDC, 0, 0, Win32.SRCCOPY);
        }
        finally
        {
            Win32.EndPaint(_hwnd, ref ps);
        }
    }

    /// <summary>建立/重建離屏合成緩衝區——只在 client area 尺寸真的變了才重建(CreateCompatibleDC/
    /// CreateDIBSection 不便宜,每次 OnPaint 都重建也會失去緩衝本身的意義)。</summary>
    private void EnsureBackBuffer(int width, int height)
    {
        if (_backBufferDC != IntPtr.Zero && width == _backBufferWidth && height == _backBufferHeight)
            return;

        FreeBackBuffer();

        // CreateCompatibleDC(IntPtr.Zero) 本來就是「跟目前螢幕相容」,不需要另外借一個真的
        // 視窗 DC 來查——而且這個方法是從 OnPaint 內部呼叫的,OnPaint 自己已經包了一層
        // BeginPaint/EndPaint,在裡面再呼叫一次 BeginPaint 是不合法的巢狀呼叫(這是我自己
        // 寫的時候手滑多繞的一步,刪掉)。
        _backBufferDC = Win32.CreateCompatibleDC(IntPtr.Zero);
        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down,跟畫面其他地方的慣例一致
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
        };
        _backBufferBitmap = Win32.CreateDIBSection(IntPtr.Zero, ref bmi, Win32.DIB_RGB_COLORS, out _, IntPtr.Zero, 0);
        _backBufferOldBitmap = Win32.SelectObject(_backBufferDC, _backBufferBitmap);
        _backBufferWidth = width;
        _backBufferHeight = height;
    }

    private void FreeBackBuffer()
    {
        if (_backBufferDC == IntPtr.Zero) return;
        Win32.SelectObject(_backBufferDC, _backBufferOldBitmap);
        Win32.DeleteObject(_backBufferBitmap);
        Win32.DeleteDC(_backBufferDC);
        _backBufferDC = IntPtr.Zero;
        _backBufferBitmap = IntPtr.Zero;
        _backBufferOldBitmap = IntPtr.Zero;
        _backBufferWidth = 0;
        _backBufferHeight = 0;
    }

    /// <summary>橫跨整個 client area 頂部或底部的一條深色 banner + 白字,蓋在停格畫面上面——
    /// 不是真的半透明(GDI 要做半透明得另外疊一張 AlphaBlend 離屏 bitmap,犧牲視覺效果換
    /// 簡單可靠),但畫面主體還看得到,不會整個被蓋住,符合計畫 §8「保留最後一幀」的用意。
    /// <see cref="DrawReconnectingBanner"/>/<see cref="DrawHotkeyHintBanner"/> 共用同一套畫法
    /// (code review 抓到的坑:這段 GDI 畫法原本被複製貼上了兩份,只有文字/位置不一樣)。</summary>
    private void DrawBanner(IntPtr hdc, string text, bool atTop)
    {
        Win32.GetClientRect(_hwnd, out var client);
        int bannerHeight = Math.Min(56, Math.Max(32, (client.bottom - client.top) / 8));
        var banner = atTop
            ? new Native.RECT { left = client.left, right = client.right, top = client.top, bottom = client.top + bannerHeight }
            : new Native.RECT { left = client.left, right = client.right, top = client.bottom - bannerHeight, bottom = client.bottom };

        var brush = Win32.CreateSolidBrush(0x00202020); // 深灰,COLORREF 是 0x00BBGGRR
        try { Win32.FillRect(hdc, ref banner, brush); }
        finally { Win32.DeleteObject(brush); }

        Win32.SetBkMode(hdc, Win32.TRANSPARENT);
        Win32.SetTextColor(hdc, 0x00FFFFFF); // 白字
        Win32.DrawText(hdc, text, -1, ref banner, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
    }

    private void DrawReconnectingBanner(IntPtr hdc) =>
        DrawBanner(hdc, "連線中斷,正在嘗試重新連線…", atTop: false);

    /// <summary>開窗頭幾秒疊的熱鍵說明。<paramref name="atTop"/> 由呼叫端依目前 HUD 位置決定,
    /// 避開 HUD 畫的那個角落(不能固定畫頂部——HUD 預設就在 top-left,固定畫頂部反而把使用者
    /// 要看的連線狀況蓋住,這是 code review 抓到的真坑)。過了 <see cref="_hotkeyHintUntil"/>
    /// 就不會再被呼叫,不用額外清除。</summary>
    private void DrawHotkeyHintBanner(IntPtr hdc, bool atTop) =>
        DrawBanner(hdc, "Ctrl+Alt+Shift+C 啟用/停用控制　F9 切換滑鼠模式　F10 切換 HUD　Ctrl+Alt+Shift+Q 離開", atTop);

    /// <summary>計畫 §4/§8 HUD——依 <see cref="_hudState"/> 決定要畫哪幾塊、依
    /// <see cref="RdpB.Core.Windows.HudSettings.Position"/> 決定疊在哪個角落,同一個角落
    /// 有多塊時由靠螢幕邊緣的那塊開始往內疊(bottom 時由下往上、top 時由上往下)。</summary>
    private void DrawHudBlocks(IntPtr hdc)
    {
        var blocks = new List<string[]> { BuildConnectionLines() };
        if (_hudState == HudState.All) blocks.Add(BuildCodecLines());

        Win32.GetClientRect(_hwnd, out var client);
        bool right = _hudPosition is "top-right" or "bottom-right";
        bool bottom = _hudPosition is "bottom-left" or "bottom-right";

        int fontSize = Math.Clamp(_hudFontSize, 8, 32);
        int lineHeight = fontSize + 8;
        byte alpha = (byte)Math.Clamp(_hudOpacity * 255, 0, 255);
        const int padding = 8, boxWidth = 280, gap = 6, margin = 8;

        int cursorY = bottom ? client.bottom - margin : margin;
        var ordered = bottom ? Enumerable.Reverse(blocks) : blocks;

        foreach (var lines in ordered)
        {
            int boxHeight = padding * 2 + lineHeight * lines.Length;
            int top = bottom ? cursorY - boxHeight : cursorY;
            int left = right ? client.right - margin - boxWidth : margin;

            DrawHudBox(hdc, left, top, boxWidth, boxHeight, lines, lineHeight, padding, alpha, _hudFont);

            cursorY = bottom ? top - gap : top + boxHeight + gap;
        }
    }

    private string[] BuildConnectionLines()
    {
        string proxyText = _usedProxy ? "是" : "否";
        string camouflageText = _usedProxy ? "—(走代理,不適用)" : _usedRdpCamouflage ? "是" : "否";
        string rttText = _lastRttMs < 0 ? "量測中…" : $"{_lastRttMs:0} ms";
        string reconnectText = _session.ReconnectCount > 0 ? $"{_session.ReconnectCount} 次" : "0 次";
        // Kyra 回饋的真坑:控制要不要轉送給 Host 是靠熱鍵切換(計畫 §5),但畫面上完全沒有
        // 任何提示——使用者開了串流視窗、滑鼠鍵盤卻完全沒反應,看起來像「控制不了」的 bug,
        // 其實是沒按熱鍵啟用。熱鍵說明本來就該在應用內看得到,不是只寫在程式碼註解/計畫文件裡。
        string controlText = _input.State.IsActive ? "已啟用" : "未啟用(按 Ctrl+Alt+Shift+C 啟用)";

        return
        [
            $"傳輸:{_transportKind}",
            $"代理:{proxyText}　偽裝:{camouflageText}",
            $"RTT:{rttText}",
            $"重連:{reconnectText}",
            $"控制:{controlText}",
        ];
    }

    /// <summary>「協議/編解碼細節」區塊——Host 端的編碼器/目標 fps/位元率來自
    /// <see cref="_hostStreamInfo"/>(串流剛開始時可能還沒收到,顯示「量測中…」),
    /// 其餘是 Client 自己量的(解碼器種類、實際解析度、實際 fps)。</summary>
    private string[] BuildCodecLines()
    {
        var info = _hostStreamInfo;
        int w, h;
        lock (_frameLock) { w = _frameWidth; h = _frameHeight; }

        return
        [
            $"Host 編碼器:{info?.Encoder ?? "量測中…"}",
            $"Client 解碼器:{_decoder.DecoderKind}",
            $"解析度:{(w > 0 && h > 0 ? $"{w}x{h}" : "量測中…")}",
            $"fps:實際 {_actualFps:0.#} / 目標 {(info is null ? "—" : info.TargetFps.ToString())}",
            $"位元率目標:{(info is null ? "—" : $"{info.TargetBitrateKbps} Kbps")}",
        ];
    }

    private static void DrawHudBox(IntPtr hdc, int left, int top, int width, int height,
        string[] lines, int lineHeight, int padding, byte alpha, IntPtr font)
    {
        var box = new Native.RECT { left = left, top = top, right = left + width, bottom = top + height };
        FillRectAlpha(hdc, box, 0x00202020, alpha);

        IntPtr oldFont = font != IntPtr.Zero ? Win32.SelectObject(hdc, font) : IntPtr.Zero;
        Win32.SetBkMode(hdc, Win32.TRANSPARENT);
        Win32.SetTextColor(hdc, 0x00FFFFFF);
        for (int i = 0; i < lines.Length; i++)
        {
            var lineRect = new Native.RECT
            {
                left = box.left + padding,
                right = box.right - padding,
                top = box.top + padding + i * lineHeight,
                bottom = box.top + padding + (i + 1) * lineHeight,
            };
            Win32.DrawText(hdc, lines[i], -1, ref lineRect, Win32.DT_SINGLELINE | Win32.DT_VCENTER);
        }
        if (oldFont != IntPtr.Zero) Win32.SelectObject(hdc, oldFont);
    }

    /// <summary>真的用 AlphaBlend 做半透明(不是「連線中斷」banner 那種故意簡化成 solid
    /// 色塊)——先建一塊跟目標矩形同尺寸的記憶體 DC + 32bpp DIB section,填滿
    /// premultiplied-alpha 的背景色,再用 msimg32.dll 的 AlphaBlend 疊到真正的畫面 DC 上。
    /// <paramref name="colorBgr"/> 是 COLORREF 格式(0x00BBGGRR,跟 CreateSolidBrush 一致)。</summary>
    private static void FillRectAlpha(IntPtr hdc, Native.RECT rect, uint colorBgr, byte alpha)
    {
        int w = rect.right - rect.left, h = rect.bottom - rect.top;
        if (w <= 0 || h <= 0) return;

        IntPtr memDc = Win32.CreateCompatibleDC(hdc);
        if (memDc == IntPtr.Zero) return;
        try
        {
            var bmi = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Win32.BI_RGB,
            };
            IntPtr dib = Win32.CreateDIBSection(memDc, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return;
            try
            {
                IntPtr oldBmp = Win32.SelectObject(memDc, dib);
                try
                {
                    // AlphaBlend 的來源 bitmap 要求是 premultiplied alpha——每個色版要先乘過
                    // alpha/255 才寫進去,不是存原始顏色再讓 AlphaBlend 自己乘。整塊填同一個
                    // 顏色+同一個 alpha,直接算好填滿最簡單,不用逐像素判斷。
                    byte b = (byte)(colorBgr & 0xFF), g = (byte)((colorBgr >> 8) & 0xFF), r = (byte)((colorBgr >> 16) & 0xFF);
                    byte pb = (byte)(b * alpha / 255), pg = (byte)(g * alpha / 255), pr = (byte)(r * alpha / 255);

                    var buffer = new byte[w * h * 4];
                    for (int i = 0; i < w * h; i++)
                    {
                        buffer[i * 4 + 0] = pb;
                        buffer[i * 4 + 1] = pg;
                        buffer[i * 4 + 2] = pr;
                        buffer[i * 4 + 3] = alpha;
                    }
                    Marshal.Copy(buffer, 0, bits, buffer.Length);

                    var blend = new Win32.BLENDFUNCTION
                    {
                        BlendOp = Win32.AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255, // 整體透明度已經 premultiply 進每個像素了,這裡不用再乘一次
                        AlphaFormat = 1, // AC_SRC_ALPHA
                    };
                    Win32.AlphaBlend(hdc, rect.left, rect.top, w, h, memDc, 0, 0, w, h, blend);
                }
                finally { Win32.SelectObject(memDc, oldBmp); }
            }
            finally { Win32.DeleteObject(dib); }
        }
        finally { Win32.DeleteDC(memDc); }
    }

    private void OnFrameDecoded(DecodedFrame frame)
    {
        lock (_frameLock)
        {
            _frameBgra = frame.Bgra;
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
        }
        Interlocked.Increment(ref _framesDecodedSinceLastTick); // OnHudTick 結算「實際 fps」用
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
                //
                // code review 複查時發現跟 WsTransport 那個漏掉上限檢查是同一類坑:這個佇列
                // 原本完全沒有深度上限——如果本機解碼跟不上收到的畫面速度(機器卡、GPU 忙),
                // 會無限塞下去吃記憶體。送出端(ChannelSendQueue)早就有「Video 佇列深度超過
                // 門檻就丟最舊的」這個政策(計畫 §1「防延遲累積」),收這端補上同一個數字,
                // 行為一致:dequeue 不是為了處理,是為了丟掉。
                _pendingVideoPackets.Enqueue(payload.ToArray());
                while (_pendingVideoPackets.Count > ChannelPolicy.QueueDepth(Channel.Video) && _pendingVideoPackets.TryDequeue(out _)) { }
                if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_DECODE_PENDING, IntPtr.Zero, IntPtr.Zero);
                break;

            case Channel.Audio when type == MessageType.AudioFrame:
                try { _audioPlayer.SubmitFrame(payload.Span); }
                catch (Exception ex) { RdpB.Core.Diagnostics.AppLog.WriteLine($"[StreamWindow] 音訊播放失敗:{ex}"); }
                break;

            case Channel.Control when type == MessageType.Bye:
                // Host 主動斷線前附上的原因(見 HostListenerService.SendByeAsync 上的說明)——
                // 這是在 Session 的讀取迴圈執行緒上到的,彈 MessageBox 這種會跑自己訊息迴圈的
                // 操作不該直接在這條執行緒上做,存起來、轉送到視窗自己的執行緒(跟
                // WM_TAMPERING_DETECTED 同一個模式)。
                _byeReason = System.Text.Encoding.UTF8.GetString(payload.Span);
                if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_BYE_RECEIVED, IntPtr.Zero, IntPtr.Zero);
                break;

            case Channel.Control when type == MessageType.ConfigSync:
                // §8 HUD「協議/編解碼細節」區塊要顯示的 Host 端資訊(編碼器/目標 fps/位元率)
                // ——這是在 Session 的讀取迴圈執行緒上到的,只是存一個欄位,不用排進任何佇列
                // 或切換到視窗執行緒(跟 _lastRttMs 同一個模式,短暫的 stale read 無害)。
                if (HostStreamInfo.TryParse(payload.Span, out var info))
                {
                    _hostStreamInfo = info;
                    if (_hwnd != IntPtr.Zero && _hudState != HudState.Off) Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
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
            catch (Exception ex)
            {
                RdpB.Core.Diagnostics.AppLog.WriteLine($"[StreamWindow] 解碼失敗:{ex}");
                // 跟重連時同一個道理(見 OnSessionStateChanged 的註解):解碼器一旦因為缺參考幀
                // 卡住,不會自己恢復,不是只有「斷線重連」才會發生——Video channel 本來就是
                // droppable(§1「防延遲累積」,佇列塞滿會主動丟最舊的幀),單純網路壅塞也可能
                // 掉幀造成同樣的狀況。每次解碼失敗都要一張關鍵幀,但不要每個封包都送一次
                // (解碼失敗往往是連續一整串,全部送出去只是洗爆 Control channel),節流到最多
                // 每秒一次。
                var now = DateTime.UtcNow;
                if (now - _lastKeyframeRequestAt > TimeSpan.FromSeconds(1))
                {
                    _lastKeyframeRequestAt = now;
                    SendInput(Channel.Video, MessageType.VideoKeyframeRequest, []);
                }
            }
        }
    }

    private DateTime _lastKeyframeRequestAt = DateTime.MinValue;

    private void OnSessionStateChanged(RdpB.Core.Session.SessionState state)
    {
        if (_hwnd == IntPtr.Zero) return;

        switch (state)
        {
            case RdpB.Core.Session.SessionState.Reconnecting:
                Win32.SetWindowText(_hwnd, $"{_title}(連線中斷,正在嘗試重新連線…)");
                _isReconnecting = true;
                Win32.InvalidateRect(_hwnd, IntPtr.Zero, false); // 立刻補畫 banner,不用等下一次自然重繪
                break;
            case RdpB.Core.Session.SessionState.Active:
                Win32.SetWindowText(_hwnd, _title);
                _isReconnecting = false;
                Win32.InvalidateRect(_hwnd, IntPtr.Zero, false); // 接回來了,把 banner 擦掉
                // 真的在這台機器上炸出來的坑:斷線期間 Host 還在送幀,SafeSend 遇到斷掉的
                // transport 只是安靜吞掉(見 HostListenerService.SafeSend 上的註解),不會
                // 重送——這段期間的幀就這樣憑空消失。接回來之後 Host 繼續照 GOP 送 P-frame,
                // 但那些 P-frame 參照的是 Client 從沒收到過的畫面,解碼器從此卡死在
                // 「Invalid data found when processing input」,而且永遠不會自己恢復,因為
                // 從來沒有人跟 Host 要一張新的關鍵幀。VideoKeyframeRequest 這個訊息型別和
                // Host 端的處理(CaptureEncoder.RequestKeyframe)本來就有,只是 Client 端
                // 從來沒送過——重連成功這一刻正是該送的時機,逼 Host 立刻出一張 IDR 讓
                // 解碼器重新同步,不用等它自己(其實永遠不會)恢復。
                SendInput(Channel.Video, MessageType.VideoKeyframeRequest, []);
                break;
            case RdpB.Core.Session.SessionState.Closed:
                // 重連視窗(預設 30 秒)真的放棄了才關視窗,不是一斷線就關——跟 Session 本身
                // 的語意一致,不用在這裡自己另外算一次逾時。
                Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
        }
    }

    /// <summary>StreamWindow 自己的 RegisterHotKey 組合(Ctrl+Alt+Shift+C/Q、F9、F10)要讓
    /// InputCaptureController 的鍵盤 hook 放行、不攔截轉送——不然 Active 時這些鍵會被吞掉
    /// 送去給 Host,熱鍵的 WM_HOTKEY 永遠收不到,人會被鎖在遠端畫面裡跳不出來。</summary>
    private static bool IsReservedHotkeyCombo(Native.KBDLLHOOKSTRUCT info)
    {
        if (info.vkCode == Native.VK_F9 || info.vkCode == Native.VK_F10) return true;

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
        catch (ObjectDisposedException) { } // 預期中的競態(視窗正在關閉時還有輸入事件飄進來)
        catch (Exception ex)
        {
            // 跟 HostListenerService.SafeSend 同一類坑:這裡原本吞掉任何其他例外,一個字都
            // 不留。還是不能讓它砸死輸入擷取,但至少留個痕跡,不要讓「鍵鼠送不出去」這種
            // 真正出乎意料的錯誤完全沒有線索可查。
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[StreamWindow] SendInputAsync 遇到非預期的例外:{ex}");
        }
    }

    private void CleanupResources()
    {
        if (_closed) return;
        _closed = true;

        _session.MessageReceived -= OnSessionMessageReceived;
        _session.StateChanged -= OnSessionStateChanged;
        _session.RttMeasured -= OnRttMeasured;

        Win32.UnregisterHotKey(_hwnd, HotkeyToggleControl);
        Win32.UnregisterHotKey(_hwnd, HotkeyExit);
        Win32.UnregisterHotKey(_hwnd, HotkeyToggleMouseMode);
        Win32.UnregisterHotKey(_hwnd, HotkeyToggleHud);

        if (_antiTamper is not null)
        {
            _antiTamper.TamperingDetected -= OnTamperingDetected;
            _antiTamper.Dispose();
        }

        _hudTickTimer?.Dispose();
        if (_hudFont != IntPtr.Zero) Win32.DeleteObject(_hudFont);
        FreeBackBuffer();

        _gamepad.Dispose();
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
