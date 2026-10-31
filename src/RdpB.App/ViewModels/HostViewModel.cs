using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RdpB.App.Services;
using RdpB.Core.Windows;

namespace RdpB.App.ViewModels;

/// <summary>計畫 §8「本機當 Host」面板:一個開關啟動/停止,畫面上顯示 PIN、監聽埠、
/// 目前連進來的 Client 數。邏輯都在 <see cref="HostListenerService"/>,這裡只是把它
/// 轉成 Avalonia 綁得到的屬性。</summary>
public sealed partial class HostViewModel : ObservableObject
{
    private readonly HostListenerService _service;
    private readonly string _configPath;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _pin;
    [ObservableProperty] private int _port;
    [ObservableProperty] private int _connectedClientCount;

    /// <summary>Kyra 回報的真坑:這台機器當 Host 要監聽哪個埠,以前完全沒有地方可以改——
    /// <c>HostListenerService.Start</c> 雖然有 <c>port</c> 參數,但沒有任何呼叫端傳過不是
    /// 預設值的數字。這個欄位是「使用者想要的設定值」,跟上面的 <see cref="Port"/>(Host
    /// 實際在跑時,TCP listener 真的綁到的那個號碼,port=0 時由系統配置隨機埠)是兩回事——
    /// 兩者平常應該一致,但改這個欄位之後要重啟 Host 才會真的套用(跟 frp 設定同一個慣例),
    /// 所以故意分開存,不要讓改了設定但還沒重啟時,畫面上的「監聽埠」顯示跟著亂跳。</summary>
    [ObservableProperty] private int _listenPortSetting;
    [ObservableProperty] private string? _listenPortSavedMessage;

    /// <summary>真的有人回報過的坑:<see cref="HostListenerService.Start"/> 裡面的 QUIC
    /// listener 啟動在某些機器上會卡住(見該方法上的註解,現在已經補了 5 秒逾時),但原本
    /// 這裡是在 Avalonia UI 執行緒上同步呼叫,逾時補了也沒用——使用者按下「啟動 Host」的
    /// 那幾秒(甚至卡住的話更久)整個視窗會「沒有回應」。改成丟到背景執行緒跑,這個旗標讓
    /// 畫面上至少看得出「正在啟動,不是當掉了」,按鈕也能順便擋掉重複點擊。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    private bool _isStarting;

    public string StartButtonText => IsStarting ? "啟動中…" : "啟動 Host";

    /// <summary>「邏輯性/安全性/意外測試」複查時抓到的坑:HostListenerService.Start() 裡
    /// 主監聽(TCP+TLS)的 bind 完全沒包 try/catch(QUIC/RDP 偽裝/WS 三個次要監聽都有,
    /// 唯獨這個真正會讓整個 Host 開不起來的主監聽沒有)——最常見的失敗原因是埠號已經被佔用
    /// (例如不小心開了兩個 RdpB.exe、或 Kyra 自己先用別的程式佔了那個埠)。原本 Start()
    /// 丟出來的例外在 StartAsync 裡完全沒接,會一路往外飄到 RelayCommand 底下,
    /// CommunityToolkit.Mvvm 的 AsyncRelayCommand 預設不會把例外顯示出來——Kyra 按下
    /// 「啟動 Host」,畫面上什麼都沒發生,IsRunning 還是 false,但完全不知道為什麼失敗。
    /// 補上這個欄位,讓失敗原因至少看得到。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastError))]
    private string? _lastError;

    public bool HasLastError => !string.IsNullOrEmpty(LastError);

    /// <summary>「允許預匹配模式」:這個值不會隨 Host 開/關而變(跟著身分憑證走,不是連線
    /// 狀態),建構時就能讀到,不用等按下「啟動 Host」。</summary>
    public string? FingerprintHex => _service.FingerprintHex;

    // ── frp 隧道(Kyra 要求的功能,見 FrpTunnelService 類別上的註解)──────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFrpEnabledAndRunning))]
    [NotifyPropertyChangedFor(nameof(IsFrpEnabledButNotRunning))]
    private bool _frpEnabled;
    [ObservableProperty] private string _frpServerAddr = "";
    [ObservableProperty] private int _frpServerPort = 7000;
    [ObservableProperty] private string _frpToken = "";
    [ObservableProperty] private int _frpRemotePort = 17443;
    [ObservableProperty] private string? _frpSavedMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFrpEnabledAndRunning))]
    [NotifyPropertyChangedFor(nameof(IsFrpEnabledButNotRunning))]
    private bool _frpIsRunning;

    // 「狀態」分類頁的概覽區塊用(Kyra 回饋:狀態頁看不到其他分類的設定狀態)——
    // AXAML 的 MultiBinding/BoolConverters 要另外 import 命名空間,不如直接在這裡算好,
    // 一個 bool 對一個 IsVisible,不用在 XAML 裡疊邏輯。
    public bool IsFrpEnabledAndRunning => FrpEnabled && FrpIsRunning;
    public bool IsFrpEnabledButNotRunning => FrpEnabled && !FrpIsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrpHasLastError))]
    private string? _frpLastError;

    public bool FrpHasLastError => !string.IsNullOrEmpty(FrpLastError);

    // ── 已信任裝置(撤銷用,紅隊測試+手動安全審查都點名的缺口)──────────────────
    public ObservableCollection<TrustedClientCardViewModel> TrustedClients { get; } = new();
    public bool HasNoTrustedClients => TrustedClients.Count == 0;

    // ── 連線密碼(Kyra 要求的無人值守加固,見 ConnectionPasswordSettings 上的註解)────
    [ObservableProperty] private bool _connectionPasswordEnabled;
    [ObservableProperty] private string _connectionPasswordInput = ""; // 寫入專用,存檔後立刻清空,不回填明文
    [ObservableProperty] private string? _connectionPasswordSavedMessage;

    // ── 左側分類導覽(跟 SettingsViewModel 同一個理由:狀態/frp/已信任裝置原本疊成一條
    // 長長的 StackPanel,Kyra 回饋「所有都是一直在疊」——照這個模式拆開。</summary>
    public string[] AvailableCategories => [StatusCategory, FrpCategory, ConnectionPasswordCategory, TrustedClientsCategory];
    private const string StatusCategory = "狀態";
    private const string FrpCategory = "frp 內網穿透";
    private const string ConnectionPasswordCategory = "連線密碼";
    private const string TrustedClientsCategory = "已信任裝置";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusCategory))]
    [NotifyPropertyChangedFor(nameof(IsFrpCategory))]
    [NotifyPropertyChangedFor(nameof(IsConnectionPasswordCategory))]
    [NotifyPropertyChangedFor(nameof(IsTrustedClientsCategory))]
    private string _selectedCategory = StatusCategory;

    public bool IsStatusCategory => SelectedCategory == StatusCategory;
    public bool IsFrpCategory => SelectedCategory == FrpCategory;
    public bool IsConnectionPasswordCategory => SelectedCategory == ConnectionPasswordCategory;
    public bool IsTrustedClientsCategory => SelectedCategory == TrustedClientsCategory;

    public HostViewModel(HostListenerService? service = null, string? configPath = null)
    {
        _service = service ?? new HostListenerService();
        _configPath = configPath ?? ConfigStore.DefaultPath;

        var config = ConfigStore.Load(_configPath);
        FrpEnabled = config.Frp.Enabled;
        FrpServerAddr = config.Frp.ServerAddr;
        FrpServerPort = config.Frp.ServerPort;
        // Token 本身不回填到畫面上——跟 SettingsViewModel 的代理密碼同一個考量,顯示明文
        // token 在截圖/錄影時外流的風險比「每次都要重新輸入」更不值得,留空代表「沿用目前
        // 存的那組,除非這次有重新輸入」。
        FrpRemotePort = config.Frp.RemotePort;

        ListenPortSetting = config.HostListenPort;

        ConnectionPasswordEnabled = config.ConnectionPassword.Enabled;

        // HostListenerService.StateChanged 可能從背景的 accept loop 執行緒觸發(code review
        // 抓到的真坑:有人連上來那一刻就是背景執行緒),Avalonia 的綁定屬性只能在 UI 執行緒上
        // 改,所以一律 Dispatcher.UIThread.Post——即使剛好已經在 UI 執行緒上呼叫也是安全的,
        // 只是排一次隊,不會出錯。
        _service.StateChanged += () => Dispatcher.UIThread.Post(OnServiceStateChanged);
        TrustedClients.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoTrustedClients));

        RefreshTrustedClients();
    }

    private void OnServiceStateChanged()
    {
        IsRunning = _service.IsRunning;
        Pin = _service.Pin;
        Port = _service.Port;
        ConnectedClientCount = _service.ConnectedClientCount;
        FrpIsRunning = _service.FrpIsRunning;
        FrpLastError = _service.FrpLastError;
        RefreshTrustedClients(); // 新配對成功、或撤銷之後,StateChanged 都會觸發,清單跟著更新
    }

    /// <summary>code review 複查抓到的坑:原本每次 StateChanged 觸發(包含 frp 隧道重試、
    /// PIN 自動過期換發這類完全跟「信任清單」無關的事件)都會無條件清空重建整個
    /// ObservableCollection——frp 連不上時每 2~60 秒重試一次就會跟著閃爍重繪一次清單
    /// (每個卡片還連帶重新配置一次 Revoke 的 closure),而且 GetTrustedClients() 本身要
    /// 讀磁碟。先比對內容(指紋+上次連線時間,這兩個合起來才能真的代表「清單有沒有實質
    /// 變化」——只比指紋集合的話,同一台裝置重新連線更新 LastConnected 不會被偵測到)
    /// 沒變就不重建,只有真的有變化(新配對、撤銷、重新連線時間更新)才重畫。</summary>
    private void RefreshTrustedClients()
    {
        var latest = _service.GetTrustedClients();

        bool unchanged = latest.Count == TrustedClients.Count &&
            latest.Zip(TrustedClients, (a, b) =>
                string.Equals(a.FingerprintHex, b.Record.FingerprintHex, StringComparison.OrdinalIgnoreCase) &&
                a.LastConnected == b.Record.LastConnected).All(same => same);
        if (unchanged) return;

        TrustedClients.Clear();
        foreach (var record in latest)
            TrustedClients.Add(new TrustedClientCardViewModel(record, OnRevokeTrustedClient));
    }

    private void OnRevokeTrustedClient(TrustedClientCardViewModel card)
    {
        _service.RevokeTrustedClient(card.Record.FingerprintHex);
        RefreshTrustedClients(); // RevokeTrustedClient 自己也會觸發 StateChanged,這裡立刻刷新一次,體感更即時
    }

    [RelayCommand]
    private void RegeneratePin() => _service.RegeneratePin();

    [RelayCommand]
    private void SaveFrpSettings()
    {
        var config = ConfigStore.Load(_configPath);
        config.Frp.Enabled = FrpEnabled;
        config.Frp.ServerAddr = FrpServerAddr.Trim();
        config.Frp.ServerPort = FrpServerPort;
        config.Frp.RemotePort = FrpRemotePort;
        // 留空 = 不要去動已經存的那組 token(見建構子裡 FrpToken 不回填的理由)。
        if (!string.IsNullOrEmpty(FrpToken))
        {
            config.Frp.Token = FrpToken;
            FrpToken = ""; // 存進去之後立刻清掉畫面上的欄位,不要讓明文一直留在記憶體/畫面上
        }
        ConfigStore.Save(config, _configPath);
        FrpSavedMessage = FrpEnabled && string.IsNullOrEmpty(config.Frp.Token)
            ? "已儲存(但還沒設定 token,啟動 Host 時 frp 隧道不會真的開啟)"
            : "已儲存——下次啟動 Host 時生效(重新啟動 Host 才會套用新設定)";
    }

    /// <summary>Kyra 要求的無人值守加固(見 ConnectionPasswordSettings 上的註解)——
    /// 開著的話,就算是已經信任的裝置,每次連線都要再驗一次這組密碼。即時生效,不用重開
    /// Host(HostListenerService 每次連線都重新 ConfigStore.Load 讀最新值,見那邊的註解)。</summary>
    [RelayCommand]
    private void SaveConnectionPasswordSettings()
    {
        var config = ConfigStore.Load(_configPath);

        if (ConnectionPasswordEnabled)
        {
            if (ConnectionPasswordInput.Length == 0 && config.ConnectionPassword.Password is null)
            {
                ConnectionPasswordSavedMessage = "要啟用連線密碼,請先輸入一組密碼";
                return;
            }
            if (ConnectionPasswordInput.Length > 0)
            {
                config.ConnectionPassword.Password = ConnectionPasswordInput;
                ConnectionPasswordInput = ""; // 存進去之後立刻清掉畫面上的欄位,不要讓明文一直留在記憶體/畫面上
            }
            config.ConnectionPassword.Enabled = true;
            ConnectionPasswordSavedMessage = "已啟用——已信任的裝置之後每次連線都要再輸入這組密碼。";
        }
        else
        {
            config.ConnectionPassword.Enabled = false;
            ConnectionPasswordInput = "";
            ConnectionPasswordSavedMessage = "已關閉——已信任的裝置恢復成不用再驗證就能連線。";
        }

        ConfigStore.Save(config, _configPath);
    }

    [RelayCommand]
    private void SaveListenPort()
    {
        if (ListenPortSetting is < 1 or > 65535)
        {
            ListenPortSavedMessage = "埠號要在 1~65535 之間";
            return;
        }

        var config = ConfigStore.Load(_configPath);
        config.HostListenPort = ListenPortSetting;
        ConfigStore.Save(config, _configPath);
        ListenPortSavedMessage = _service.IsRunning
            ? "已儲存——Host 現在還在用舊的埠,重新啟動 Host 才會換成這個"
            : "已儲存——下次啟動 Host 時生效";
    }

    [RelayCommand]
    private async Task ToggleHostAsync()
    {
        if (_service.IsRunning)
            await StopAsync();
        else
            await StartAsync(ListenPortSetting);
    }

    public async Task StartAsync(int port = Core.Transport.DefaultPorts.Host)
    {
        if (IsStarting || _service.IsRunning) return;
        IsStarting = true;
        LastError = null;
        try
        {
            // _service.Start() 本身是同步方法,裡面有真的會花時間的工作(TCP/QUIC/WS 監聽
            // 都要 bind)——丟到背景執行緒跑,UI 執行緒不會被卡住,不管 Start() 到底要花
            // 多久(正常情況下很快,但見上面 IsStarting 屬性上的註解)。
            await Task.Run(() => _service.Start(port)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 主監聽 bind 失敗(最常見:埠號被佔用)會從這裡冒出來——一定要接住,不然會
            // 整個消失在 AsyncRelayCommand 底下,Kyra 完全不知道按鈕按下去為什麼沒反應。
            LastError = DescribeStartFailure(ex);
        }
        finally
        {
            IsStarting = false;
        }
        OnServiceStateChanged();
    }

    private static string DescribeStartFailure(Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse
            => $"啟動失敗:埠號已經被佔用(可能已經開了另一個 RdpB.exe,或被別的程式用掉了)。{se.Message}",
        System.Net.Sockets.SocketException se => $"啟動失敗:網路層錯誤。{se.Message}",
        _ => $"啟動失敗:{ex.Message}",
    };

    public async Task StopAsync()
    {
        await _service.StopAsync();
        OnServiceStateChanged();
    }
}
