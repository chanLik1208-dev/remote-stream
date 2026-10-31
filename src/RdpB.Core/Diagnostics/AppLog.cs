namespace RdpB.Core.Diagnostics;

/// <summary>
/// 計畫 §8 診斷頁的「log 檢視」那一半——原本整個方案裡散落的
/// <c>System.Diagnostics.Debug.WriteLine(...)</c> 只在 Debug 組態下編譯進去,Release
/// build(Kyra 實際在用的那份)完全看不到,等於這些花心思寫的錯誤訊息(QUIC 開不起來、
/// frp 隧道失敗、偽裝監聽撞埠……)全部白寫,使用者永遠看不到。
///
/// 改成這個放在 <c>RdpB.Core</c>(最底層、不綁 Windows,每個專案都能直接參照)的輕量
/// in-memory 環狀緩衝——不寫檔案(計畫沒要求長期保存 log,寫檔案還要考慮輪替/清理,
/// 這次範圍先不做),只保留最近 <see cref="Capacity"/> 筆,夠診斷頁即時看、夠覆蓋「剛剛
/// 發生的那次失敗」就好。<see cref="EntryAdded"/> 讓診斷頁可以即時更新,不用自己 polling。
///
/// **範圍老實講清楚**:這次只把既有的 17 個 Debug.WriteLine 呼叫點全部換成呼叫這裡,
/// 不是幫整個方案加上一套全面的結構化 logging 框架——那是更大的工程,這次先解決「原本寫的
/// 診斷訊息在 Release build 底下完全看不到」這個具體問題。</summary>
public static class AppLog
{
    public sealed record Entry(DateTimeOffset Time, string Message);

    private const int Capacity = 500;

    private static readonly object Lock = new();
    private static readonly Queue<Entry> Entries = new();

    /// <summary>診斷頁訂閱這個事件即時更新,不用自己啟動一個 Timer 去 polling
    /// <see cref="Snapshot"/>。在呼叫 <see cref="WriteLine"/> 的那個執行緒上觸發——Host 端
    /// 的訊息常常來自背景執行緒,訂閱端(Avalonia ViewModel)自己要負責切回 UI 執行緒,
    /// 這裡不做這件事(跟 HostListenerService.StateChanged 的既有慣例一致)。</summary>
    public static event Action<Entry>? EntryAdded;

    public static void WriteLine(string message)
    {
        var entry = new Entry(DateTimeOffset.Now, message);
        lock (Lock)
        {
            Entries.Enqueue(entry);
            while (Entries.Count > Capacity) Entries.Dequeue();
        }
        EntryAdded?.Invoke(entry);
    }

    /// <summary>診斷頁開啟時先拿這份補上「開啟之前已經發生過的事」,之後再靠
    /// <see cref="EntryAdded"/> 跟上新進來的。</summary>
    public static IReadOnlyList<Entry> Snapshot()
    {
        lock (Lock) return Entries.ToArray();
    }
}
