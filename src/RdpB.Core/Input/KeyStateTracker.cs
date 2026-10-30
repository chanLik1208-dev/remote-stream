namespace RdpB.Core.Input;

/// <summary>
/// 記錄目前「按著」的鍵和滑鼠按鈕,兩端(Client 擷取端、Host 注入端)各自維護一份。
///
/// 這是修掉 remote-stream 操作端 bug 的核心(計畫 §5):舊版全域 hook 不看焦點、
/// Alt+Tab/Ctrl+Alt+Del/斷線都會卡鍵,因為沒有人追蹤「現在到底按著什麼」、也沒有
/// 任何機制在異常情況發生時主動放開。
///
/// Client 端:在 WM_KILLFOCUS / WM_ACTIVATE(inactive) / 切換控制 / 斷線時呼叫
///   <see cref="ReleaseAll"/>,拿到一份「現在按著的全部鍵」清單,逐一送出放開事件
///   (而不是只送一個 RELEASE_ALL 訊號就假設對面會自己清——雙邊都要有狀態才保險)。
/// Host 端:收到 RELEASE_ALL、心跳逾時、或 session 結束時,呼叫 <see cref="ReleaseAll"/>
///   把實際按著的鍵透過 SendInput 全部放開(雙保險,即使 Client 端那波事件漏送也沒事)。
/// </summary>
public sealed class KeyStateTracker
{
    private readonly HashSet<ushort> _downKeys = new();
    private readonly HashSet<byte> _downMouseButtons = new();
    private readonly Lock _lock = new();

    public void OnKeyDown(ushort scanCode)
    {
        lock (_lock) _downKeys.Add(scanCode);
    }

    public void OnKeyUp(ushort scanCode)
    {
        lock (_lock) _downKeys.Remove(scanCode);
    }

    public void OnMouseButtonDown(byte button)
    {
        lock (_lock) _downMouseButtons.Add(button);
    }

    public void OnMouseButtonUp(byte button)
    {
        lock (_lock) _downMouseButtons.Remove(button);
    }

    public bool IsKeyDown(ushort scanCode)
    {
        lock (_lock) return _downKeys.Contains(scanCode);
    }

    public int DownKeyCount
    {
        get { lock (_lock) return _downKeys.Count; }
    }

    public int DownMouseButtonCount
    {
        get { lock (_lock) return _downMouseButtons.Count; }
    }

    /// <summary>
    /// 清空狀態並回傳「清空前還按著的東西」,呼叫端要用這份清單逐一送出放開事件
    /// (注入到系統,或透過網路通知對面)。清單是快照,之後再呼叫不會重複回報已放開的鍵。
    /// </summary>
    public ReleaseSnapshot ReleaseAll()
    {
        lock (_lock)
        {
            var keys = _downKeys.ToArray();
            var buttons = _downMouseButtons.ToArray();
            _downKeys.Clear();
            _downMouseButtons.Clear();
            return new ReleaseSnapshot(keys, buttons);
        }
    }

    public readonly record struct ReleaseSnapshot(ushort[] Keys, byte[] MouseButtons)
    {
        public bool IsEmpty => Keys.Length == 0 && MouseButtons.Length == 0;
    }
}
