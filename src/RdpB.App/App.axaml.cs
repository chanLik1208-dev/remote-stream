using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;
using RdpB.App.Services;
using RdpB.App.ViewModels;
using RdpB.App.Views;

namespace RdpB.App;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _viewModel;
    private bool _reallyExiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _viewModel = new MainWindowViewModel();
            _mainWindow = new MainWindow { DataContext = _viewModel };
            desktop.MainWindow = _mainWindow;
            FilePickerService.StorageProvider = _mainWindow.StorageProvider;
            PinDialogService.Owner = _mainWindow;

            SetupTrayIcon(desktop);

            // UX 回饋:縮到系統匣改成自動化行為,由 Settings.MinimizeToTray 這個設定統一控制
            // (預設開,使用者可以在設定頁關掉改成「按 X 直接結束」)——不再只看 Host 是否在跑,
            // Host 開著時這個設定還是能照使用者的選擇直接結束程式,是他們自己的決定。
            // _reallyExiting 區分「使用者按 X」跟「系統匣選單按結束」,後者一定要真的退出。
            //
            // code review 抓到的真坑:選了「直接結束」又剛好 Host 在跑,不能真的讓視窗直接關掉
            // ——那樣 TcpListener/accept loop 會被粗暴丟著,不是乾淨關閉。要先等 Host.StopAsync()
            // 做完,才放行真正的 Close()。
            _mainWindow.Closing += async (_, e) =>
            {
                if (_reallyExiting) return;

                if (_viewModel.Settings.MinimizeToTray)
                {
                    e.Cancel = true;
                    _mainWindow.Hide();
                    return;
                }

                if (_viewModel.Host.IsRunning)
                {
                    e.Cancel = true;
                    await _viewModel.Host.StopAsync();
                    _reallyExiting = true;
                    _mainWindow.Close();
                }
                // 不是 MinimizeToTray、Host 也沒在跑 → 不攔,正常關閉。
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _trayIcon = new TrayIcon
        {
            Icon = TrayIconFactory.CreateDefaultIcon(),
            ToolTipText = "RDP-B(Better)",
            Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem("開啟主視窗") { Command = new RelayCommand(ShowMainWindow) },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem("結束") { Command = new AsyncRelayCommand(() => ExitAppAsync(desktop)) },
                },
            },
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private async Task ExitAppAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // 同一個理由:系統匣選單按「結束」時 Host 若在跑,也要先乾淨關閉,不能直接丟著
        // (code review 抓到的缺口,原本這條路徑完全沒停過 Host)。
        if (_viewModel?.Host.IsRunning == true)
            await _viewModel.Host.StopAsync();

        _reallyExiting = true;
        _trayIcon?.Dispose();
        desktop.Shutdown();
    }
}
