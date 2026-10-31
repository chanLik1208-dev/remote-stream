using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;
using RdpB.App.Services;
using RdpB.App.ViewModels;
using RdpB.App.Views;
using RdpB.Core.Windows;

namespace RdpB.App;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _viewModel;
    private bool _reallyExiting;
    private IncognitoSession? _incognito;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 啟動模式的決定過程本身是非同步的(可能要先跳一個視窗問),但
            // OnFrameworkInitializationCompleted 這個 override 本身是同步的——拆成一個
            // async 方法,fire-and-forget 呼叫。
            _ = InitializeAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // 「設定加密了就每次進應用就要求輸入密碼」(Kyra 的原話)——這一定要是
        // InitializeAsync 裡**第一件事**,比「問一般/無痕模式」那段還早,因為那段本身
        // 一開始就可能呼叫 ConfigStore.Load(見下面第 60 行左右)去讀 DefaultLaunchMode——
        // 如果 config.json 是加密的,沒先解鎖就呼叫 Load 會直接丟 InvalidOperationException。
        if (!await EnsureConfigUnlockedAsync(desktop).ConfigureAwait(true))
        {
            desktop.Shutdown();
            return;
        }

        // 「應用內確定,或者打開應用自動開啟」(Kyra 的原話)——命令列參數還是保留給
        // 熟悉的使用者/系統匣重開那條路用,但一般雙擊啟動不用再記命令列參數:
        //  - 有 --incognito/-i 或 --normal:命令列優先,略過問答跟「記住的選擇」。
        //  - 沒有命令列參數、DefaultLaunchMode 記住的是 normal/incognito:直接套用,不問
        //    (「自動開啟」那一半)。
        //  - 沒有命令列參數、DefaultLaunchMode 是預設的 "ask":跳出選擇視窗問一次,
        //    勾了「記住」才會把這次選擇存進真正的 config.json,下次開啟不用再問
        //    (「應用內確定」那一半)。
        bool? incognitoFromArgs = desktop.Args switch
        {
            { } args when args.Any(a => a is "--incognito" or "-i") => true,
            { } args when args.Any(a => a is "--normal") => false,
            _ => null,
        };

        bool incognito;
        if (incognitoFromArgs is not null)
        {
            incognito = incognitoFromArgs.Value;
        }
        else
        {
            var realConfig = ConfigStore.Load(ConfigStore.DefaultPath);
            incognito = realConfig.DefaultLaunchMode switch
            {
                "incognito" => true,
                "normal" => false,
                _ => await AskLaunchModeAsync(desktop).ConfigureAwait(true),
            };
        }

        string? configPath = null;
        if (incognito)
        {
            _incognito = IncognitoSession.Start();
            configPath = _incognito.ConfigPath;
        }

        _viewModel = new MainWindowViewModel(configPath);
        _mainWindow = new MainWindow { DataContext = _viewModel };
        if (incognito)
            _mainWindow.Title = $"{_mainWindow.Title}(無痕模式——這個 session 不會寫回真正的設定檔)";
        desktop.MainWindow = _mainWindow;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose; // 選擇視窗(如果有跳出來過)關掉時不該觸發整個 app 結束,交還成正常的「主視窗關了才算結束」。
        FilePickerService.StorageProvider = _mainWindow.StorageProvider;
        PinDialogService.Owner = _mainWindow;

        _mainWindow.Show();

        SetupTrayIcon(desktop, incognito);

        // 不管走哪條退出路徑(按 X 直接結束、系統匣「結束」、或任何其他方式),
        // Exit 事件都保證會在真正關閉前觸發一次——無痕模式的 temp 設定檔複本在這裡
        // 統一清掉,不用在每一條退出路徑各自記得呼叫一次。
        desktop.Exit += (_, _) =>
        {
            _incognito?.Dispose();
            // mihomo 是純本機行程(不像 frp 要跟遠端登記資源),直接 Kill 完全安全——
            // 見 MihomoProxyService 類別上的註解。不收掉的話,app 關了它還會繼續留著連訂閱。
            _viewModel.Settings.StopMihomoAsync().GetAwaiter().GetResult();
        };

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

    /// <summary>沒加密直接回傳 true(多數使用者從頭到尾都不會碰到這個對話框)。加密了就跳
    /// <see cref="UnlockConfigDialog"/>——這個時間點 MainWindow 還沒建出來,一樣要暫時改
    /// ShutdownMode 避免「唯一的視窗關掉」被 Avalonia 當成整個 app 要結束(跟
    /// AskLaunchModeAsync 同一個理由)。使用者按「取消」或密碼就是解不開,回傳 false,
    /// 呼叫端負責真的結束程式——沒有後門,解不開就是用不了。</summary>
    private static async Task<bool> EnsureConfigUnlockedAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!ConfigStore.IsEncrypted(ConfigStore.DefaultPath)) return true;

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var dialog = new UnlockConfigDialog();
        var closedTcs = new TaskCompletionSource();
        dialog.Closed += (_, _) => closedTcs.TrySetResult();
        dialog.Show();
        await closedTcs.Task.ConfigureAwait(true);

        return dialog.Unlocked;
    }

    /// <summary>跳選擇視窗問一次「一般/無痕」,回傳 true=無痕。這個時間點 MainWindow 還沒
    /// 建出來,選擇視窗沒有 owner 可以掛——用 ShutdownMode.OnExplicitShutdown 暫時擋住
    /// 「這是目前唯一的視窗,關掉就等於整個 app 結束」這個 Avalonia 預設行為,問完之後
    /// 呼叫端會把 ShutdownMode 换回正常值。</summary>
    private static async Task<bool> AskLaunchModeAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var chooser = new LaunchModeWindow();
        var closedTcs = new TaskCompletionSource();
        chooser.Closed += (_, _) => closedTcs.TrySetResult();
        chooser.Show();
        await closedTcs.Task.ConfigureAwait(true);

        var choice = chooser.Result;
        if (choice is null) return false; // 直接按叉關掉選擇視窗 = 當作選了一般模式,不要卡住

        if (choice.Remember)
        {
            var realConfig = ConfigStore.Load(ConfigStore.DefaultPath);
            realConfig.DefaultLaunchMode = choice.Incognito ? "incognito" : "normal";
            ConfigStore.Save(realConfig, ConfigStore.DefaultPath); // 一定寫真正的 config.json,不是任何無痕複本
        }

        return choice.Incognito;
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, bool incognito)
    {
        var menu = new NativeMenu
        {
            Items =
            {
                new NativeMenuItem("開啟主視窗") { Command = new RelayCommand(ShowMainWindow) },
            },
        };

        // 只在一般模式下提供這個選項——已經在無痕模式裡就不用再給一次(而且「無痕模式裡
        // 再開一個無痕模式」沒有意義,徒增困惑)。不開新流程直接改現有流程的狀態(例如清掉
        // _viewModel、重建視窗)風險比較高——用 Process.Start 開一個新的 RdpB.exe 行程,
        // 比較乾淨,舊的這個照原本流程正常結束。
        if (!incognito)
        {
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(new NativeMenuItem("以無痕模式開啟新視窗")
            {
                Command = new RelayCommand(LaunchIncognitoInstance),
            });
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("結束") { Command = new AsyncRelayCommand(() => ExitAppAsync(desktop)) });

        _trayIcon = new TrayIcon
        {
            Icon = TrayIconFactory.CreateDefaultIcon(),
            ToolTipText = incognito ? "RDP-B(Better)——無痕模式" : "RDP-B(Better)",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    /// <summary>開一個全新的 RdpB.exe 行程、帶 --incognito——刻意是「新視窗」不是「重新啟動
    /// 這個視窗」,兩個 session 可以同時開著(例如一般模式當 Host 長期掛著,另外開一個無痕
    /// 模式的 Client 去連別人借的電腦,互不影響)。</summary>
    private static void LaunchIncognitoInstance()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "--incognito")
            {
                UseShellExecute = true,
            });
        }
        catch { /* 開不起來(例如找不到 exe)不該讓現在這個正常運作中的程式跟著出事 */ }
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
