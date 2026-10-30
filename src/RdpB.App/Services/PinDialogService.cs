using Avalonia.Controls;
using Avalonia.Threading;
using RdpB.App.Views;

namespace RdpB.App.Services;

/// <summary>讓 <see cref="ClientConnectionService"/>(完全不碰 Avalonia)可以跳出 PIN 輸入視窗,
/// 又不用直接持有 Window 參考——跟 <see cref="FilePickerService"/> 一樣的模式,App.axaml.cs
/// 在建好主視窗後把它塞進來當 owner。</summary>
public static class PinDialogService
{
    public static Window? Owner { get; set; }

    /// <summary>ClientConnectionService 跑在背景 Task 上,跳對話框一定要切回 UI 執行緒——
    /// 不然 Avalonia 會直接丟例外,不是「畫面怪怪的」這種軟性問題。</summary>
    public static Task<string?> RequestPinAsync(string hostName)
    {
        if (Owner is null) return Task.FromResult<string?>(null);

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new PinDialog();
            dialog.SetPrompt($"在「{hostName}」那邊確認下面的 PIN,然後在這裡輸入:");
            return await dialog.ShowDialog<string?>(Owner).ConfigureAwait(false);
        });
    }
}
