namespace RdpB.Core.Media;

/// <summary>
/// 計畫 §8「畫質」設定裡的 fps 選項——GUI 的下拉選單、CaptureEncoder 的參數驗證都用同一份
/// 清單,不要兩邊各自硬編一次數字,免得哪天改了其中一邊忘記改另一邊。
/// </summary>
public static class FrameRateOptions
{
    public static readonly int[] AllowedValues = [30, 45, 60, 90, 120, 144];

    public const int Default = 60;

    public static bool IsValid(int fps) => Array.IndexOf(AllowedValues, fps) >= 0;

    /// <summary>不合法就丟例外,訊息裡直接列出允許的值,不用猜。</summary>
    public static void Validate(int fps, string paramName = "fps")
    {
        if (!IsValid(fps))
            throw new ArgumentOutOfRangeException(paramName, fps,
                $"fps 只能是 {string.Join(" / ", AllowedValues)} 其中之一");
    }
}
