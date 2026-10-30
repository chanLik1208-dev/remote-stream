namespace RdpB.Core.Input;

public enum MouseMode { Absolute, Relative }

/// <summary>
/// Client 端「要不要擷取鍵鼠」的決策邏輯,跟 Win32 P/Invoke 完全分開,方便寫測試。
/// 計畫 §5 列的操作端 bug,根源都是「沒有單一個地方決定現在到底算不算在擷取」,
/// 這個類別就是那個單一來源——Win32 的 WndProc/hook 只負責把事件丟進來、把決定拿去執行。
///
/// 規則:
///  - 只有 <see cref="IsActive"/> 的時候才擷取/攔截本機輸入。
///  - 任何讓 Active 變 false 的路徑(切換熱鍵、失去焦點、斷線)都要回傳一份
///    <see cref="KeyStateTracker.ReleaseSnapshot"/>,呼叫端拿去放開本機攔截、
///    並通知對面 Host 也放開——不會有「漏了某條路徑忘記放開」的情況。
/// </summary>
public sealed class CaptureStateMachine
{
    private readonly KeyStateTracker _keys = new();

    public bool IsActive { get; private set; }
    public MouseMode MouseMode { get; private set; } = MouseMode.Absolute;
    public KeyStateTracker Keys => _keys;

    /// <summary>切換熱鍵按下那一刻呼叫(呼叫端自己判斷「是按下的那一刻」,自動連發不算——
    /// 計畫§5「只看按下的那一刻」)。回傳新的 Active 狀態。</summary>
    public bool ToggleControl()
    {
        if (IsActive)
        {
            IsActive = false;
            return false; // 呼叫端看到 false 時應該呼叫 ReleaseAllAndGetSnapshot()
        }
        IsActive = true;
        return true;
    }

    public MouseMode ToggleMouseMode()
    {
        MouseMode = MouseMode == MouseMode.Absolute ? MouseMode.Relative : MouseMode.Absolute;
        return MouseMode;
    }

    /// <summary>WM_KILLFOCUS / WM_ACTIVATE(inactive) / 視窗關閉 / 斷線時呼叫。
    /// 不管目前是不是 Active,都強制關掉並回傳要放開的鍵——重複呼叫是安全的
    /// (底層 KeyStateTracker.ReleaseAll 第二次呼叫回傳空快照)。</summary>
    public KeyStateTracker.ReleaseSnapshot Deactivate()
    {
        IsActive = false;
        return _keys.ReleaseAll();
    }
}
