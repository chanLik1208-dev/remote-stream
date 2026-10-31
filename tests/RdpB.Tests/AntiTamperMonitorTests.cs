using System.Runtime.InteropServices;
using RdpB.Client;
using Xunit;

namespace RdpB.Tests;

/// <summary>
/// 「反注入,升級攻擊者的成本」這個功能的驗證——見 AntiTamperMonitor 上的註解,這是
/// best-effort,不是保證防住。這裡只驗證兩件事:正常情況(只有啟動時就載入的模組)不誤判,
/// 以及真的有新模組從「不是程式目錄/系統目錄」的路徑載入時會被抓到。
/// </summary>
public sealed class AntiTamperMonitorTests
{
    [Fact]
    public void Scan_WithOnlyBaselineModulesLoaded_DoesNotReportTampering()
    {
        var monitor = new AntiTamperMonitor();
        bool reported = false;
        monitor.TamperingDetected += _ => reported = true;

        // 連續跑幾輪,模擬背景 Timer 多次觸發——測試執行環境本身在這段時間不會有新模組載入,
        // 這是驗證「正常情況下不會自己亂報」的基本要求。
        for (int i = 0; i < 3; i++) monitor.Scan();

        Assert.False(reported);
        monitor.Dispose();
    }

    [Fact]
    public void Scan_AfterLoadingNativeModuleFromUntrustedDirectory_ReportsTampering()
    {
        // 模擬「被注入了一個不是從程式目錄/系統目錄載入的模組」——複製一支系統內建的原生
        // DLL(winmm.dll,跟這個測試的業務邏輯完全無關,純粹只是借來測「路徑比對」這個機制)
        // 到一個跟程式目錄無關的暫存資料夾,用 NativeLibrary.Load 真的把它載入進這個程序
        // (一定會透過 LoadLibrary,一定會出現在 Process.Modules 裡,跟 managed assembly
        // 不一定會走 Win32 載入器是不同的——這裡要驗證的是路徑比對邏輯,不是找一個方便的檔案)。
        var systemDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "winmm.dll");
        var tempDir = Path.Combine(Path.GetTempPath(), "RdpBTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var untrustedDll = Path.Combine(tempDir, "winmm_copy.dll");

        try
        {
            if (!File.Exists(systemDll))
                return; // 不同 Windows 版本路徑不保證一致,不是這個測試該驗證的事,跳過不當失敗。

            File.Copy(systemDll, untrustedDll);

            var monitor = new AntiTamperMonitor();
            string? reportedReason = null;
            monitor.TamperingDetected += reason => reportedReason = reason;

            NativeLibrary.Load(untrustedDll);
            monitor.Scan();

            Assert.NotNull(reportedReason);
            Assert.Contains(untrustedDll, reportedReason, StringComparison.OrdinalIgnoreCase);
            monitor.Dispose();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
