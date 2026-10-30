using Avalonia.Controls;
using RdpB.App.ViewModels;

namespace RdpB.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // UX 回饋:不要另外放一個「縮到系統匣」按鈕,縮小視窗這個動作本身就自動收到系統匣
        // (受 Settings.MinimizeToTray 控制,使用者可以在設定頁關掉改成直接結束)。
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty || WindowState != WindowState.Minimized) return;
            if (DataContext is not MainWindowViewModel vm || !vm.Settings.MinimizeToTray) return;

            Hide();
            WindowState = WindowState.Normal; // 下次 Show() 才不會是縮小狀態
        };
    }
}
