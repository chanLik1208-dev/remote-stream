// RemotePlay — 原生輸入中繼 (Track A)
//   client 模式：用全域 low-level hook 擷取本機鍵鼠，序列化成 UDP 封包送到主機。
//   host   模式：接收 UDP 封包，用 SendInput 注入到本機（跑遊戲那台）。
//
// 用法:
//   RemotePlay host  [--input-port 9001]
//   RemotePlay client --host <ip|name> [--input-port 9001] [--toggle F8]
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RemotePlay;

// ── 線路協定（little-endian）───────────────────────────────
//   [0]=type
//   KEY(1)      : [1..2] u16 scanCode, [3] flags(bit0=down, bit1=extended)
//   MOUSEABS(2) : [1..2] u16 x(0..65535), [3..4] u16 y(0..65535)   歸一化到主螢幕
//   MBTN(3)     : [1] button(0=L,1=R,2=M,3=X1,4=X2), [2] down(1/0)
//   WHEEL(4)    : [1..2] i16 delta(120=一格), [3] horizontal(0/1)
//   MOUSEREL(5) : [1..2] i16 dx, [3..4] i16 dy   FPS 相對位移(raw input，不重置游標)
//   HELLO(0)    : 客戶端連線通知(讓主機學到客戶端 IP,供音訊回送)
//   PAD(6)      : [1..2] u16 buttons, [3] LT, [4] RT, [5..12] 4x i16 thumb(LX,LY,RX,RY)
static class Proto
{
    public const byte HELLO = 0, KEY = 1, MOUSEABS = 2, MBTN = 3, WHEEL = 4, MOUSEREL = 5, PAD = 6;
}

static class Program
{
    static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 某些主控台不支援時忽略 */ }
        if (args.Length == 0) return Menu();          // 雙擊時的互動選單
        var opts = ParseOpts(args, 1);
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "host": return HostRole.Run(opts);
                case "client": return ClientRole.Run(opts);
                default: Usage(); return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Fatal: " + ex.Message);
            return 2;
        }
    }

    static int Menu()
    {
        Console.WriteLine("=== RemotePlay 遠端串流 ===");
        Console.WriteLine("[1] 主機（被控 / 跑遊戲，需 NVIDIA + ffmpeg）");
        Console.WriteLine("[2] 客戶端（操作端，需 ffplay）");
        Console.Write("選擇 (1/2): ");
        var r = Console.ReadLine()?.Trim();
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (r == "2")
        {
            Console.Write("主機 IP: ");
            var ip = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(ip)) { Console.WriteLine("需要主機 IP。"); Pause(); return 1; }
            o["host"] = ip!;
            Console.Write("全螢幕? (y/N): ");
            if (string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) o["fullscreen"] = "true";
            return ClientRole.Run(o);
        }
        if (r == "1")
        {
            Console.Write("用 UPnP 自動開路由器埠?（跨網路才需要, y/N): ");
            if (string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) o["upnp"] = "true";
            return HostRole.Run(o);
        }
        Console.WriteLine("已取消。"); Pause(); return 1;
    }

    static void Pause() { Console.WriteLine("按任意鍵結束…"); try { Console.ReadKey(); } catch { } }

    static void Usage()
    {
        Console.WriteLine("用法:");
        Console.WriteLine("  RemotePlay host   [--port 9000] [--input-port 9001] [--codec h264|hevc|av1]");
        Console.WriteLine("                    [--bitrate 30M] [--fps 60] [--latency 40] [--monitor 0]");
        Console.WriteLine("                    [--no-input] [--no-audio] [--upnp]");
        Console.WriteLine("  RemotePlay client --host <ip> [--port 9000] [--input-port 9001] [--codec h264]");
        Console.WriteLine("                    [--latency 40] [--mouse rel|abs] [--toggle F8] [--toggle-mouse F9]");
        Console.WriteLine("                    [--fullscreen] [--hwdecode] [--no-input] [--no-audio] [--no-gamepad]");
        Console.WriteLine("  （不帶參數雙擊 = 互動選單）");
    }

    // 解析 --key value / --flag
    static Dictionary<string, string> ParseOpts(string[] args, int start)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = start; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { d[key] = args[++i]; }
            else d[key] = "true";
        }
        return d;
    }

    public static string? Get(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v : null;
    public static int GetInt(Dictionary<string, string> o, string k, int def)
        => o.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : def;
}

// ─────────────────────────── HOST：影像 + 輸入 + 音訊 + 手把 ───────────────────────────
static class HostRole
{
    static volatile bool _stop;
    static Process? _cur;

    // 自動測上傳頻寬 → 取 ~60% 當位元率（保留餘裕給重傳/音訊/輸入）
    static string AutoBitrate()
    {
        Console.WriteLine("[host] 測試上傳頻寬中…（約 5-15 秒）");
        double up = Bandwidth.TestUploadMbps();
        if (up <= 0) { Console.WriteLine("[host] 頻寬測試失敗，改用 8M"); return "8M"; }
        int kbps = (int)Math.Clamp(up * 1000 * 0.6, 2000, 40000);
        Console.WriteLine($"[host] 上傳約 {up:F1} Mbps → 影像位元率設 {kbps / 1000.0:F1}M");
        return kbps + "k";
    }

    public static int Run(Dictionary<string, string> o)
    {
        int videoPort = Program.GetInt(o, "port", 9000);
        int inputPort = Program.GetInt(o, "input-port", 9001);
        int audioPort = inputPort + 1;
        string codec = Program.Get(o, "codec") ?? "h264";
        string bitrate;
        if (Program.Get(o, "bitrate") is string manual) bitrate = manual;   // 手動指定優先
        else if (o.ContainsKey("autobitrate")) bitrate = AutoBitrate();       // 自動測上傳
        else bitrate = "30M";                                                 // 區網預設
        int fps = Program.GetInt(o, "fps", 60);
        int latency = Program.GetInt(o, "latency", 40);
        int monitor = Program.GetInt(o, "monitor", 0);
        bool noInput = o.ContainsKey("no-input");
        bool noAudio = o.ContainsKey("no-audio");
        bool upnp = o.ContainsKey("upnp");
        int[] ports = { videoPort, inputPort };

        Firewall.Ensure(new[] { videoPort, inputPort, audioPort }); // 讓輸入/音訊封包進得來
        bool upnpOpened = upnp && Upnp.Open(ports);

        AudioSender? audio = noAudio ? null : new AudioSender(new UdpClient());
        HostInput? input = noInput ? null : new HostInput(inputPort, audio, audioPort);
        input?.Start();
        audio?.Start();

        string ff = Tools.Find("ffmpeg");
        var args = MediaArgs.Host(monitor, fps, codec, bitrate, videoPort, latency);
        Console.WriteLine($"[host] 串流 {codec}@{fps}fps {bitrate} → SRT :{videoPort}（latency {latency}ms）");
        Console.WriteLine($"[host] 客戶端連線：RemotePlay client --host <本機IP> --port {videoPort} --input-port {inputPort} --codec {codec}");
        Console.WriteLine("[host] 等待客戶端連線…（Ctrl+C 結束）");

        int rc = 0;
        _stop = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _stop = true; try { if (_cur is { HasExited: false }) _cur.Kill(true); } catch { } };
        try
        {
            while (!_stop)
            {
                _cur = Proc.Start(ff, args);   // 找不到 ffmpeg 時第一次即丟 Win32Exception
                _cur.WaitForExit();
                if (_stop) break;
                Console.WriteLine($"[host] ⚠ 串流中斷（ffmpeg exit {_cur.ExitCode}）。3 秒後自動重啟，等待客戶端重連…（Ctrl+C 結束）");
                for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(@"[host] 找不到 ffmpeg，請確認已安裝並在 PATH（或放到 exe 旁的 ffmpeg\ 資料夾）。");
            rc = 3;
        }
        finally
        {
            input?.Stop();
            audio?.Stop();
            if (upnpOpened) { Console.WriteLine("[host] 收回 UPnP 埠對應"); Upnp.Close(ports); }
        }
        return rc;
    }
}

// 主機輸入伺服器：收 UDP → 學客戶端 IP（供音訊回送）→ 注入鍵鼠 / 灌虛擬手把
sealed class HostInput
{
    readonly int _port, _audioPort;
    readonly AudioSender? _audio;
    UdpClient? _udp;
    Thread? _t;
    volatile bool _run = true;
    bool _learned, _gotInput;

    public HostInput(int port, AudioSender? audio, int audioPort) { _port = port; _audio = audio; _audioPort = audioPort; }

    public void Start()
    {
        _udp = new UdpClient(_port);
        _t = new Thread(Loop) { IsBackground = true };
        _t.Start();
        Console.WriteLine($"[host] 輸入伺服器 UDP :{_port}");
    }

    public void Stop() { _run = false; try { _udp?.Close(); } catch { } }

    void Loop()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (_run)
        {
            byte[] d;
            try { d = _udp!.Receive(ref ep); }
            catch { if (_run) continue; else break; }

            if (!_learned)
            {
                _learned = true;
                _audio?.SetDest(new IPEndPoint(ep.Address, _audioPort));
                Console.WriteLine($"[host] 客戶端已連線：{ep.Address}（音訊回送 → :{_audioPort}）");
            }
            if (d.Length < 1 || d[0] == Proto.HELLO) continue;
            if (!_gotInput) { _gotInput = true; Console.WriteLine("[host] ✔ 收到第一個輸入事件，開始注入（客戶端已按 F8 控制）"); }
            try
            {
                if (d[0] == Proto.PAD) GamepadSink.Handle(d);
                else Injector.Handle(d);
            }
            catch (Exception ex) { Console.WriteLine("[host] 注入例外（略過此封包）：" + ex.Message); }
        }
    }
}

static class Injector
{
    public static void Handle(byte[] d)
    {
        if (d.Length < 1) return;
        switch (d[0])
        {
            case Proto.KEY when d.Length >= 4:
                Native.SendKey((ushort)(d[1] | (d[2] << 8)), (d[3] & 1) != 0, (d[3] & 2) != 0);
                break;
            case Proto.MOUSEABS when d.Length >= 5:
                Native.MouseAbs((ushort)(d[1] | (d[2] << 8)), (ushort)(d[3] | (d[4] << 8)));
                break;
            case Proto.MOUSEREL when d.Length >= 5:
                Native.MouseRel((short)(d[1] | (d[2] << 8)), (short)(d[3] | (d[4] << 8)));
                break;
            case Proto.MBTN when d.Length >= 3:
                Native.MouseBtn(d[1], d[2] != 0);
                break;
            case Proto.WHEEL when d.Length >= 4:
                Native.Wheel((short)(d[1] | (d[2] << 8)), d[3] != 0);
                break;
        }
    }
}

// ─────────────────────────── CLIENT：擷取並轉送 ───────────────────────────
static class ClientRole
{
    static UdpClient _udp = null!;
    static IPEndPoint _host = null!;
    static volatile bool _active;         // 是否正在控制主機
    static volatile bool _relative;       // true=FPS 相對位移, false=絕對座標
    static ushort _toggleVk = 0x77;       // F8：控制開/關
    static ushort _modeVk = 0x78;         // F9：切換絕對/相對滑鼠
    static int _cw, _ch;
    static volatile bool _cstop;
    static Process? _cplay;
    static IntPtr _kbHook, _msHook, _rawWnd;
    // 保留 delegate 參考，避免被 GC 回收導致 callback 失效
    static readonly Native.HookProc _kbProc = KbProc;
    static readonly Native.HookProc _msProc = MsProc;
    static readonly Native.WndProc _wndProc = RawWndProc;

    public static int Run(Dictionary<string, string> o)
    {
        string host = Program.Get(o, "host") ?? throw new ArgumentException("--host 必填");
        int videoPort = Program.GetInt(o, "port", 9000);
        int inputPort = Program.GetInt(o, "input-port", 9001);
        int audioPort = inputPort + 1;
        string codec = Program.Get(o, "codec") ?? "h264";
        int latency = Program.GetInt(o, "latency", 40);
        // 解碼器自動識別:偵測到 NVDEC(nvcuvid.dll)就硬解,否則軟解。可用 --hwdecode/--software-decode 強制。
        bool sw;
        if (o.ContainsKey("hwdecode")) sw = false;
        else if (o.ContainsKey("software-decode")) sw = true;
        else
        {
            IntPtr nv = Native.LoadLibrary("nvcuvid.dll");
            sw = nv == IntPtr.Zero;
            if (nv != IntPtr.Zero) Native.FreeLibrary(nv);
            Console.WriteLine(sw ? "[client] 自動識別：無 NVDEC → 軟體解碼" : "[client] 自動識別：偵測到 NVDEC → 硬體解碼");
        }
        bool fs = o.ContainsKey("fullscreen");
        bool noInput = o.ContainsKey("no-input");
        bool noAudio = o.ContainsKey("no-audio");
        bool noPad = o.ContainsKey("no-gamepad");

        if (!noAudio) Firewall.Ensure(new[] { audioPort }); // 音訊為主機→客戶端,客戶端需開此入站埠

        // ── 控制 + 音訊先建立起來（不受影像斷線影響）──
        AudioReceiver? audio = null;
        GamepadSender? pad = null;
        if (!noInput)
        {
            SetupInput(host, inputPort, o);
            if (!noPad) { pad = new GamepadSender(_udp, _host); pad.Start(); }
        }
        if (!noAudio) { audio = new AudioReceiver(audioPort); audio.Start(); }

        // ── 影像 ffplay：斷線自動重試（主機沒開/網路掉時不會整個結束）──
        string pl = Tools.Find("ffplay");
        _cstop = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cstop = true; try { if (_cplay is { HasExited: false }) _cplay.Kill(true); } catch { } };
        try
        {
            while (!_cstop)
            {
                try { _cplay = Proc.Start(pl, MediaArgs.Client(host, videoPort, codec, latency, sw, fs)); }
                catch (System.ComponentModel.Win32Exception)
                {
                    Console.Error.WriteLine("[client] 找不到 ffplay，請確認 ffmpeg 已安裝並在 PATH。");
                    return 3;
                }
                Console.WriteLine($"[client] 影像視窗已開：srt://{host}:{videoPort}（{codec}{(sw ? " 軟解" : " NVDEC 硬解")}）");
                _cplay.WaitForExit();
                if (_cstop) break;
                Console.WriteLine("[client] ⚠ 影像中斷（主機還沒開或網路斷）。3 秒後自動重試…（Ctrl+C 結束）");
                for (int i = 0; i < 30 && !_cstop; i++) Thread.Sleep(100);
            }
        }
        finally
        {
            pad?.Stop();
            audio?.Stop();
            try { if (_cplay is { HasExited: false }) _cplay.Kill(true); } catch { }
        }
        return 0;
    }

    // 設定鍵鼠擷取：建 socket、送 HELLO、安裝 hook（在有訊息迴圈的執行緒）
    static void SetupInput(string host, int inputPort, Dictionary<string, string> o)
    {
        _toggleVk = ParseVk(Program.Get(o, "toggle") ?? "F8");
        _modeVk = ParseVk(Program.Get(o, "toggle-mouse") ?? "F9");
        _relative = !string.Equals(Program.Get(o, "mouse"), "abs", StringComparison.OrdinalIgnoreCase);

        var addr = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _udp = new UdpClient();
        _host = new IPEndPoint(addr, inputPort);
        _cw = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXSCREEN));
        _ch = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYSCREEN));
        try { _udp.Send(new[] { Proto.HELLO }, 1, _host); } catch { } // 讓主機學到本機 IP（音訊回送）

        string toggle = Program.Get(o, "toggle") ?? "F8";
        string modeKey = Program.Get(o, "toggle-mouse") ?? "F9";
        Console.WriteLine($"[client] [{toggle}] 控制主機 開/關   [{modeKey}] 滑鼠 相對(FPS)/絕對 切換");
        Console.WriteLine($"[client] 目前滑鼠：{(_relative ? "相對/FPS" : "絕對/桌面")}；控制開啟時本機鍵鼠被攔截，脫困再按 {toggle} 或 Ctrl+Alt+Del。");

        var t = new Thread(HookThread) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void HookThread()
    {
        IntPtr hmod = Native.GetModuleHandle(null);
        _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, hmod, 0);
        _msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _msProc, hmod, 0);
        if (_kbHook == IntPtr.Zero || _msHook == IntPtr.Zero)
        {
            Console.Error.WriteLine("[client] ❌ 安裝鍵鼠 hook 失敗 err=" + Marshal.GetLastWin32Error() + "（防毒軟體可能擋了；試試關閉或以管理員執行）");
            return;
        }
        Console.WriteLine("[client] ✔ 鍵鼠 hook 已安裝 → 按 F8 開始控制主機");

        // 建立 message-only 視窗來收 Raw Input（WM_INPUT），供 FPS 相對位移使用
        _rawWnd = Native.CreateRawInputWindow(_wndProc, hmod);
        if (_rawWnd == IntPtr.Zero || !Native.RegisterRawMouse(_rawWnd))
            Console.Error.WriteLine("[client] Raw Input 註冊失敗 err=" + Marshal.GetLastWin32Error() + "（相對/FPS 模式可能無法運作）");

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    // Raw Input 視窗訊息處理：只取滑鼠相對位移
    static IntPtr RawWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_INPUT && _active && _relative)
        {
            if (Native.ReadRawMouseDelta(lParam, out int dx, out int dy) && (dx != 0 || dy != 0))
            {
                short sx = (short)Math.Clamp(dx, short.MinValue, short.MaxValue);
                short sy = (short)Math.Clamp(dy, short.MinValue, short.MaxValue);
                Send(new[] { Proto.MOUSEREL, (byte)(sx & 0xff), (byte)((sx >> 8) & 0xff), (byte)(sy & 0xff), (byte)((sy >> 8) & 0xff) });
            }
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // 進入相對模式：把本機游標釘在螢幕中央的 1px，讓它不亂跑、不誤點
    static void EnterRelative()
    {
        var r = new Native.RECT { left = _cw / 2, top = _ch / 2, right = _cw / 2 + 1, bottom = _ch / 2 + 1 };
        Native.ClipCursor(ref r);
    }
    static void ExitRelative() => Native.ClipCursorNull();

    static IntPtr KbProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;

            if (down && info.vkCode == _toggleVk)
            {
                _active = !_active;
                if (_active && _relative) EnterRelative(); else ExitRelative();
                Console.WriteLine(_active
                    ? $"[client] 控制 ON  → 主機（滑鼠：{(_relative ? "相對/FPS" : "絕對/桌面")}）"
                    : "[client] 控制 OFF → 操作本機");
                return (IntPtr)1; // 吃掉切換鍵
            }
            if (down && info.vkCode == _modeVk)
            {
                _relative = !_relative;
                if (_active) { if (_relative) EnterRelative(); else ExitRelative(); }
                Console.WriteLine($"[client] 滑鼠模式 → {(_relative ? "相對/FPS（raw input，不重置游標）" : "絕對/桌面")}");
                return (IntPtr)1; // 吃掉切換鍵
            }
            if (_active && (down || up))
            {
                ushort sc = (ushort)info.scanCode;
                byte flags = 0;
                if (down) flags |= 1;
                if ((info.flags & 0x01) != 0) flags |= 2; // LLKHF_EXTENDED
                Send(new[] { Proto.KEY, (byte)(sc & 0xff), (byte)(sc >> 8), flags });
                return (IntPtr)1; // 攔截本機
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static IntPtr MsProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _active)
        {
            var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case Native.WM_MOUSEMOVE:
                    if (!_relative) // 相對模式的位移改由 Raw Input(WM_INPUT) 處理
                    {
                        ushort nx = (ushort)Math.Clamp((long)m.pt.x * 65535 / (_cw - 1 == 0 ? 1 : _cw - 1), 0, 65535);
                        ushort ny = (ushort)Math.Clamp((long)m.pt.y * 65535 / (_ch - 1 == 0 ? 1 : _ch - 1), 0, 65535);
                        Send(new[] { Proto.MOUSEABS, (byte)(nx & 0xff), (byte)(nx >> 8), (byte)(ny & 0xff), (byte)(ny >> 8) });
                    }
                    break;
                case Native.WM_LBUTTONDOWN: Btn(0, true); break;
                case Native.WM_LBUTTONUP: Btn(0, false); break;
                case Native.WM_RBUTTONDOWN: Btn(1, true); break;
                case Native.WM_RBUTTONUP: Btn(1, false); break;
                case Native.WM_MBUTTONDOWN: Btn(2, true); break;
                case Native.WM_MBUTTONUP: Btn(2, false); break;
                case Native.WM_MOUSEWHEEL:
                    short d = (short)((m.mouseData >> 16) & 0xffff);
                    Send(new[] { Proto.WHEEL, (byte)(d & 0xff), (byte)((d >> 8) & 0xff), (byte)0 });
                    break;
                case Native.WM_XBUTTONDOWN: Btn((byte)(((m.mouseData >> 16) & 0xffff) == 1 ? 3 : 4), true); break;
                case Native.WM_XBUTTONUP: Btn((byte)(((m.mouseData >> 16) & 0xffff) == 1 ? 3 : 4), false); break;
            }
            return (IntPtr)1; // 攔截本機
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static void Btn(byte b, bool down) => Send(new[] { Proto.MBTN, b, (byte)(down ? 1 : 0) });

    static bool _sentInput;
    static void Send(byte[] p)
    {
        try
        {
            _udp.Send(p, p.Length, _host);
            if (!_sentInput && p.Length > 0 && p[0] != Proto.HELLO)
            {
                _sentInput = true;
                Console.WriteLine("[client] ✔ 已擷取並送出第一個輸入（F8 已開、正在控制主機）");
            }
        }
        catch { /* UDP 盡力送達 */ }
    }

    static ushort ParseVk(string s)
    {
        s = s.Trim().ToUpperInvariant();
        if (s.Length >= 2 && s[0] == 'F' && int.TryParse(s[1..], out int fn) && fn is >= 1 and <= 24)
            return (ushort)(0x70 + fn - 1);            // F1..F24
        if (s.Length == 1) return s[0];                 // A-Z / 0-9
        if (s.StartsWith("0X") && ushort.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out var h)) return h;
        return 0x77; // 預設 F8
    }
}

// ─────────────────────────── Win32 P/Invoke ───────────────────────────
static class Native
{
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const uint WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIDEV_INPUTSINK = 0x00000100, MOUSE_MOVE_ABSOLUTE = 0x01;
    static readonly IntPtr HWND_MESSAGE = new(-3);
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                     WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
                     WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeLibrary(IntPtr hModule);

    // ── Raw Input（FPS 相對位移）+ message-only 視窗 + 游標鎖定 ──
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] rid, uint num, uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] public static extern bool ClipCursor(ref RECT r);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] static extern bool ClipCursorRaw(IntPtr p);
    public static void ClipCursorNull() => ClipCursorRaw(IntPtr.Zero);

    static readonly IntPtr _rawBuf = Marshal.AllocHGlobal(64);

    public static IntPtr CreateRawInputWindow(WndProc proc, IntPtr hmod)
    {
        var wc = new WNDCLASS { lpfnWndProc = proc, hInstance = hmod, lpszClassName = "RemotePlayRawInput" };
        RegisterClassW(ref wc);
        return CreateWindowExW(0, "RemotePlayRawInput", "RemotePlayRawInput", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hmod, IntPtr.Zero);
    }

    public static bool RegisterRawMouse(IntPtr hwnd)
    {
        var rid = new[] { new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd } };
        return RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public static bool ReadRawMouseDelta(IntPtr lParam, out int dx, out int dy)
    {
        dx = 0; dy = 0;
        uint size = 64;
        int hsz = Marshal.SizeOf<RAWINPUTHEADER>();
        uint res = GetRawInputData(lParam, RID_INPUT, _rawBuf, ref size, (uint)hsz);
        if (res == unchecked((uint)-1) || res == 0) return false;
        if ((uint)Marshal.ReadInt32(_rawBuf, 0) != RIM_TYPEMOUSE) return false;
        ushort usFlags = (ushort)Marshal.ReadInt16(_rawBuf, hsz);
        if ((usFlags & MOUSE_MOVE_ABSOLUTE) != 0) return false; // 只取相對位移
        dx = Marshal.ReadInt32(_rawBuf, hsz + 12);
        dy = Marshal.ReadInt32(_rawBuf, hsz + 16);
        return true;
    }

    // ── SendInput ──
    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_RIGHTDOWN = 0x0008,
               MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
               MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;
    const uint XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int cb);

    static void Emit(INPUT i) => SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());

    public static void MouseAbs(ushort x, ushort y) => Emit(new INPUT
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } }
    });

    // FPS 相對位移：MOUSEEVENTF_MOVE 不帶 ABSOLUTE = 相對；主機遊戲用 raw input 讀取視角
    public static void MouseRel(short dx, short dy) => Emit(new INPUT
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } }
    });

    public static void MouseBtn(byte b, bool down)
    {
        uint f; uint mdata = 0;
        switch (b)
        {
            case 0: f = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            case 1: f = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case 2: f = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case 3: f = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; mdata = XBUTTON1; break;
            case 4: f = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; mdata = XBUTTON2; break;
            default: return;
        }
        Emit(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = f, mouseData = mdata } } });
    }

    public static void Wheel(short delta, bool horizontal) => Emit(new INPUT
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, mouseData = (uint)delta } }
    });

    public static void SendKey(ushort scan, bool down, bool extended)
    {
        uint f = KEYEVENTF_SCANCODE;
        if (!down) f |= KEYEVENTF_KEYUP;
        if (extended) f |= KEYEVENTF_EXTENDEDKEY;
        Emit(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = f } } });
    }
}
