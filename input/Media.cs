// 媒體協調：找 ffmpeg/ffplay、組指令、啟動程序、UPnP 開埠、取本機 IP、頻寬測試
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace RemotePlay;

// 上傳頻寬測試（POST 一塊資料到 Cloudflare speed 端點,量測吞吐）
static class Bandwidth
{
    public static double TestUploadMbps()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var payload = new byte[8 * 1024 * 1024]; // 8 MB
            var sw = Stopwatch.StartNew();
            var resp = http.PostAsync("https://speed.cloudflare.com/__up", new ByteArrayContent(payload)).GetAwaiter().GetResult();
            sw.Stop();
            resp.EnsureSuccessStatusCode();
            double secs = sw.Elapsed.TotalSeconds;
            if (secs <= 0.05) return 0;
            return payload.Length * 8.0 / secs / 1_000_000.0; // Mbps
        }
        catch { return 0; }
    }
}

static class Tools
{
    // 先找 exe 旁邊，其次 PATH
    public static string Find(string name)
    {
        string[] cands =
        {
            Path.Combine(AppContext.BaseDirectory, name + ".exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", name + ".exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", name + ".exe"),
        };
        foreach (var p in cands) if (File.Exists(p)) return p;
        return name; // 靠 PATH
    }
}

static class Proc
{
    public static Process Start(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }
}

static class MediaArgs
{
    // 主機：ddagrab 擷取 → NVENC 低延遲 → SRT listener
    public static List<string> Host(int monitor, int fps, string codec, string bitrate, int videoPort, int latency)
    {
        int gop = fps * 2;
        string enc = $"{codec}_nvenc";
        string srt = $"srt://0.0.0.0:{videoPort}?mode=listener&latency={latency}&transtype=live";
        return new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-filter_complex", $"ddagrab=output_idx={monitor}:framerate={fps}",
            "-fps_mode", "cfr",
            "-c:v", enc, "-preset", "p1", "-tune", "ll",
            "-rc", "cbr", "-b:v", bitrate, "-maxrate", bitrate,
            "-bf", "0", "-g", gop.ToString(), "-no-scenecut", "1", "-forced-idr", "1", "-delay", "0",
            "-f", "mpegts", "-flush_packets", "1", "-muxdelay", "0", "-muxpreload", "0",
            srt
        };
    }

    // 客戶端：SRT 接收 → NVDEC 硬解 → ffplay 低延遲顯示
    public static List<string> Client(string host, int videoPort, string codec, int latency, bool software, bool fullscreen)
    {
        string url = $"srt://{host}:{videoPort}?latency={latency}";
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-an",
            "-probesize", "32", "-analyzeduration", "0",
            "-fflags", "nobuffer", "-flags", "low_delay", "-framedrop",
            "-window_title", $"RemotePlay - {host}"
        };
        if (!software) { a.Add("-vcodec"); a.Add($"{codec}_cuvid"); }
        if (fullscreen) a.Add("-fs");
        a.Add(url);
        return a;
    }
}

// 防火牆:以系統管理員執行時自動開放入站 UDP 埠（輸入/音訊連不上的頭號原因）
static class Firewall
{
    public static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static void Ensure(int[] ports)
    {
        string list = string.Join(",", ports);
        if (IsAdmin())
        {
            try
            {
                Run("netsh", "advfirewall firewall delete rule name=\"remote-stream\"");
                Run("netsh", $"advfirewall firewall add rule name=\"remote-stream\" dir=in action=allow protocol=UDP localport={list}");
                Console.WriteLine($"[fw] 已開放防火牆入站 UDP {list}");
            }
            catch (Exception e) { Console.WriteLine($"[fw] 開放防火牆失敗：{e.Message}"); }
        }
        else
        {
            Console.WriteLine("[fw] ⚠ 非系統管理員 → 未自動開防火牆。若「看得到畫面但不能操控」，多半是這裡被擋。");
            Console.WriteLine($"[fw]   解法:以系統管理員重跑，或執行:  netsh advfirewall firewall add rule name=\"remote-stream\" dir=in action=allow protocol=UDP localport={list}");
        }
    }

    static void Run(string file, string args)
    {
        var psi = new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}

static class NetUtil
{
    // 取本機對外 IPv4（UDP connect 不會真的送封包）
    public static string LocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }
}

// UPnP 自動開/關路由器埠（Windows 內建 HNetCfg.NATUPnP COM）
static class Upnp
{
    static dynamic? Coll()
    {
        var t = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
        if (t == null) return null;
        dynamic nat = Activator.CreateInstance(t)!;
        return nat.StaticPortMappingCollection;
    }

    public static bool Open(int[] ports)
    {
        dynamic? c = null;
        try { c = Coll(); } catch { }
        if (c == null) { Console.WriteLine("[upnp] 路由器未回應 UPnP，略過（區網不需要）"); return false; }
        string ip = NetUtil.LocalIp();
        foreach (var p in ports)
        {
            try
            {
                try { c.Remove(p, "UDP"); } catch { }
                c.Add(p, "UDP", p, ip, true, $"remote-stream {p}");
                Console.WriteLine($"[upnp] 開埠 {p}/UDP → {ip}:{p}");
            }
            catch (Exception e) { Console.WriteLine($"[upnp] 開埠 {p} 失敗：{e.Message}"); }
        }
        return true;
    }

    public static void Close(int[] ports)
    {
        dynamic? c = null;
        try { c = Coll(); } catch { }
        if (c == null) return;
        foreach (var p in ports) { try { c.Remove(p, "UDP"); } catch { } }
    }
}
