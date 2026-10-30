namespace RdpB.Core.Protocol;

/// <summary>
/// Session 裡的多工頻道。順序即優先序(數字越小越優先),
/// Transport 送出佇列依這個順序處理:輸入 > 心跳 > 音訊 > 影像。
/// </summary>
public enum Channel : byte
{
    Control = 0,   // 配對、心跳、RELEASE_ALL、設定交換 — 可靠、不可丟
    Input = 1,     // 按鍵、滑鼠按鈕、滑鼠位移、手把 — 可靠、不可丟
    Audio = 2,     // Opus 音訊幀 — 可丟棄,但優先序高於影像
    Video = 3,     // 編碼後的影像幀 — 可丟棄,佇列滿了就丟最舊的
}

/// <summary>
/// 一個 channel 是否允許在佇列塞滿時丟棄舊資料。
/// Control/Input 永遠不丟;Audio/Video 可以丟。
/// </summary>
public static class ChannelPolicy
{
    public static bool IsDroppable(Channel ch) => ch is Channel.Audio or Channel.Video;

    /// <summary>可丟棄 channel 的佇列深度門檻(超過就丟最舊的,見計畫 §1「防延遲累積」)。</summary>
    public static int QueueDepth(Channel ch) => ch switch
    {
        Channel.Video => 2,
        Channel.Audio => 4,
        _ => int.MaxValue,
    };
}
