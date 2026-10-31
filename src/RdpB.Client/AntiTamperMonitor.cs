using System.Diagnostics;

namespace RdpB.Client;

/// <summary>
/// Kyra 要求的功能:「反注入,升級攻擊者的成本、降低他的意圖」——這裡要老實講清楚範圍:
/// 這是 best-effort 的縱深防禦,不是真的防住。攻擊者對自己的機器有完整 admin/kernel 權限的話,
/// 任何客戶端反竄改都打不贏那場仗(EAC/BattlEye 這種專業團隊都要靠簽驅動持續軍備升級才勉強跟
/// 得上)。這裡做的是「擋掉隨手可得的工具」等級,目的是提高成本、不是保證防住。
///
/// 做法:**不維護一份要人工更新的模組名單**(名單會過期、漏掉新出現的注入工具),而是用
/// 「啟動時已經載入的模組」當信任基準——這些都是正常啟動流程本來就會載入的(.NET runtime、
/// FFmpeg 的 avcodec/avfilter/… 這些 shared DLL、Avalonia 的 Skia 原生庫、系統 DLL)。之後
/// 每輪掃描只在意**新增的**模組,而且只在它不是從「自己的程式目錄」或「系統目錄/WinSxS」載入
/// 時才算可疑——合法的延遲載入(例如 .NET 之後才用到的某個 runtime 元件、GPU 驅動的編碼器
/// DLL)幾乎都躲在這兩個路徑底下,真正的注入(DLL 注入、把惡意程式碼塞進別的程序)通常是從
/// temp/AppData 之類的地方載入,路徑比對就能抓到大部分情況,不需要認得「這支 DLL 叫什麼」。
///
/// 另外同時偵測 debugger 附加(<see cref="Debugger.IsAttached"/>)——這是最基本、幾乎所有
/// 反竄改方案都會做的第一層,擋不住真正的 kernel debugger,但能擋掉隨手掛上去的使用者模式
/// debugger。
/// </summary>
public sealed class AntiTamperMonitor : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    private readonly string _appDirectory;
    private readonly string _systemDirectory;
    private readonly string _systemX86Directory;
    private readonly HashSet<string> _knownModulePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private Timer? _timer;
    private volatile bool _disposed;

    /// <summary>抓到可疑狀況時觸發,在背景 Timer 的執行緒上(不是視窗訊息迴圈執行緒)——
    /// 呼叫端(StreamWindow)要自己負責轉送到正確的執行緒,這裡不處理 UI/視窗操作。
    /// 只帶一句話描述原因,方便記 log,不帶任何更細的技術細節(萬一真的是攻擊者在觀察
    /// 反應,不需要免費告訴他是哪一步被抓到)。</summary>
    public event Action<string>? TamperingDetected;

    public AntiTamperMonitor()
    {
        _appDirectory = AppContext.BaseDirectory;
        _systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        _systemX86Directory = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

        // 啟動時的快照當基準——不是白名單,是「這些是我們自己啟動流程載入的,不是後來被塞進來的」。
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            TryGetModulePath(module, out var path);
            if (path is not null) _knownModulePaths.Add(path);
        }
    }

    public void Start() => _timer = new Timer(_ => Scan(), null, ScanInterval, ScanInterval);

    /// <summary>公開出來主要是方便寫測試(不用真的等 Timer 跑一輪)——Start() 之後背景計時器
    /// 也是呼叫這個方法,邏輯只有一份。</summary>
    public void Scan()
    {
        if (_disposed) return;

        if (Debugger.IsAttached)
        {
            Report("偵測到 debugger 附加到這個程序");
            return;
        }

        try
        {
            lock (_lock)
            {
                foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                {
                    if (!TryGetModulePath(module, out var path) || path is null) continue;
                    if (_knownModulePaths.Contains(path)) continue;

                    // 不管接下來要不要觸發警報,先記下來——同一個模組不會每輪重複觸發。
                    _knownModulePaths.Add(path);

                    if (!IsFromTrustedLocation(path))
                    {
                        Report($"偵測到來自非預期路徑的模組載入:{path}");
                        return; // 已經觸發過一次,這輪掃描到此為止,交給呼叫端決定下一步(通常是斷線)
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 掃描本身出問題(例如程序正在關閉、模組列舉時機不巧)不該被當成「偵測到竄改」,
            // 這是基礎設施層的失敗,不是安全事件。
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[AntiTamperMonitor] 掃描失敗:{ex}");
        }
    }

    private bool IsFromTrustedLocation(string path) =>
        path.StartsWith(_appDirectory, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(_systemDirectory, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(_systemX86Directory, StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\WinSxS\", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetModulePath(ProcessModule module, out string? path)
    {
        // ProcessModule.FileName 在少數情況下(模組正在被卸載、權限問題)會丟例外,
        // 不該讓單一個模組的讀取失敗中斷整輪掃描。
        try { path = module.FileName; return path is not null; }
        catch { path = null; return false; }
    }

    private void Report(string reason)
    {
        RdpB.Core.Diagnostics.AppLog.WriteLine($"[AntiTamperMonitor] {reason}");
        TamperingDetected?.Invoke(reason);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
