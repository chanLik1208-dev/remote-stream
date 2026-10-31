using Avalonia.Controls;
using RdpB.Core.Windows;

namespace RdpB.App.Views;

/// <summary>Kyra 要求的功能:「設定加密了就每次進應用就要求輸入密碼」——config.json
/// 被 <see cref="ConfigStore.EnableEncryption"/> 加密過之後,App.axaml.cs 在建立任何會讀
/// 設定檔的物件之前,一定要先讓這個對話框成功解鎖,不然整個程式沒辦法繼續(見
/// App.axaml.cs 的 EnsureConfigUnlockedAsync)。
///
/// 密碼錯誤的重試迴圈**在這個對話框自己裡面做**(顯示錯誤、清空重打,不關視窗),不是把
/// 迴圈邏輯丟給呼叫端反覆開新視窗——跟 PinDialog/LaunchModeWindow 同一個「純 code-behind,
/// 不用另開 ViewModel」的簡單模式,呼叫端只需要 Show 一次、等 Closed,然後看
/// <see cref="Unlocked"/> 是不是 true。</summary>
public partial class UnlockConfigDialog : Window
{
    public bool Unlocked { get; private set; }

    public UnlockConfigDialog()
    {
        InitializeComponent();
        PasswordBox.AttachedToVisualTree += (_, _) => PasswordBox.Focus();
        CancelButton.Click += (_, _) => Close();
        UnlockButton.Click += (_, _) =>
        {
            if (ConfigStore.TryUnlock(PasswordBox.Text ?? ""))
            {
                Unlocked = true;
                Close();
            }
            else
            {
                ErrorText.Text = "密碼不對,再試一次。";
                PasswordBox.Text = "";
                PasswordBox.Focus();
            }
        };
    }
}
