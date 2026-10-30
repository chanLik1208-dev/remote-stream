using Avalonia.Controls;

namespace RdpB.App.Views;

/// <summary>計畫 §8 的「等待配對」對話框:沒配對過的裝置按「連線」時跳出來問 PIN。
/// 純 code-behind,沒有另開 ViewModel——跟 <see cref="FilePickerService"/> 一樣的簡單
/// 靜態服務模式(見 <see cref="PinDialogService"/>),這個對話框本身沒有需要單獨測試的邏輯。
///
/// 直接按叉關掉視窗(不按「取消」或「配對」)時,Avalonia 的 ShowDialog&lt;string?&gt; 會拿到
/// null(Close() 沒帶結果的預設值)——跟按「取消」效果一樣,呼叫端不會卡著等不到結果。</summary>
public partial class PinDialog : Window
{
    public PinDialog()
    {
        InitializeComponent();
        ConnectButton.Click += (_, _) => Close(PinBox.Text);
        CancelButton.Click += (_, _) => Close(null);
        PinBox.AttachedToVisualTree += (_, _) => PinBox.Focus();
    }

    public void SetPrompt(string text) => PromptText.Text = text;
}
