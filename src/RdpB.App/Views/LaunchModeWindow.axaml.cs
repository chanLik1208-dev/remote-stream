using Avalonia.Controls;

namespace RdpB.App.Views;

/// <summary>啟動選擇視窗的結果——跟按叉關掉(直接結束程式,不是「選了一般模式」)分開,
/// 不要用 null 這種容易跟「還沒選」搞混的值。</summary>
public sealed record LaunchModeChoice(bool Incognito, bool Remember);

/// <summary>Kyra 要求「無痕模式改成可應用內確定,或者打開應用自動開啟」——這個視窗就是
/// 「應用內確定」那一半:每次開啟 app(沒有用命令列參數強制指定模式時)跳出來問,
/// 勾選「記住」就會把選擇存進真正的 config.json(DefaultLaunchMode),下次開啟直接套用、
/// 不用再選,就是使用者要的「自動開啟」那一半。
///
/// 純 code-behind,跟 PinDialog 同一個簡單模式,不需要另外開 ViewModel。</summary>
public partial class LaunchModeWindow : Window
{
    /// <summary>這個視窗出現的時候整個 app 還沒有任何其他視窗存在(是 MainWindow 蓋出來
    /// 之前的那一刻)——沒有 owner 可以用,不能走 ShowDialog&lt;T&gt;(owner) 那條路,
    /// 改成 Show() + 自己存結果,呼叫端用 Closed 事件知道什麼時候可以讀 Result。
    /// 直接按叉關掉(沒按任一個按鈕)時 Result 維持 null,呼叫端要處理這個情況
    /// (等同「使用者不想選,直接用一般模式」,不是卡住等不到回應)。</summary>
    public LaunchModeChoice? Result { get; private set; }

    public LaunchModeWindow()
    {
        InitializeComponent();
        NormalButton.Click += (_, _) =>
        {
            Result = new LaunchModeChoice(Incognito: false, Remember: RememberCheckBox.IsChecked == true);
            Close();
        };
        IncognitoButton.Click += (_, _) =>
        {
            Result = new LaunchModeChoice(Incognito: true, Remember: RememberCheckBox.IsChecked == true);
            Close();
        };
    }
}
