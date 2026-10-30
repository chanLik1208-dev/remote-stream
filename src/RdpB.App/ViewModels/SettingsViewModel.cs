using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RdpB.App.Services;
using RdpB.Core.Media;
using RdpB.Core.Windows;

namespace RdpB.App.ViewModels;

/// <summary>
/// 計畫 §8 設定頁:傳輸/畫質那兩類(RDP/HUD 留給之後的里程碑)。
/// 全域預設值(<see cref="RdpBConfig.Default"/>/<see cref="RdpBConfig.Quality"/>),
/// 每台 Host 的個別覆寫之後在裝置卡片那邊另外接。
///
/// **連線方式簡化成二級選單**(UX 回饋後改的):使用者只看得到「直連 / 透過代理」兩個選項,
/// 不再直接暴露 Core 的 auto/direct/tcp/ws——那是實作細節,不該讓使用者自己猜要選哪個。
/// 選「透過代理」才會多出第二層,問要用訂閱連結還是設定檔。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly string _configPath;
    private readonly RdpBConfig _config;

    public int[] AvailableFpsOptions => FrameRateOptions.AllowedValues;

    /// <summary>第一層:連線方式。對應 Core 的 Mode——選「透過代理」存成 "tcp"
    /// (ws 還沒實作,見 docs/PROTOCOL.md 的已知缺口)。</summary>
    public string[] AvailableConnectionKinds => [DirectLabel, ProxyLabel];
    private const string DirectLabel = "直連(DIRECT)";
    private const string ProxyLabel = "透過代理(PROXY)";

    /// <summary>第二層,只在選「透過代理」時才看得到。</summary>
    public string[] AvailableProxySourceKinds => [SubscriptionLabel, ConfigFileLabel];
    private const string SubscriptionLabel = "訂閱連結";
    private const string ConfigFileLabel = "匯入設定檔";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProxyMode))]
    [NotifyPropertyChangedFor(nameof(IsDirectMode))]
    private string _connectionKind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubscriptionSource))]
    [NotifyPropertyChangedFor(nameof(IsConfigFileSource))]
    private string _proxySourceKind;

    /// <summary>§7 的偽裝開關只在 DIRECT 才讓人看到——不只是停用,是整個藏起來
    /// (使用者回饋:走代理時這個選項不該出現在畫面上)。</summary>
    public bool IsDirectMode => ConnectionKind == DirectLabel;
    public bool IsProxyMode => ConnectionKind == ProxyLabel;
    public bool IsSubscriptionSource => ProxySourceKind == SubscriptionLabel;
    public bool IsConfigFileSource => ProxySourceKind == ConfigFileLabel;

    [ObservableProperty] private string _proxySubscriptionUrl;
    [ObservableProperty] private string _proxyConfigFilePath;
    [ObservableProperty] private bool _rdpCamouflageWhenDirect;
    [ObservableProperty] private int _fps;
    [ObservableProperty] private int _bitrateKbps;
    [ObservableProperty] private string? _savedMessage;

    /// <summary>縮小/關閉視窗時自動收到系統匣(UX 回饋:自動化行為,不要另外放按鈕)。
    /// 這個開關即時生效(MainWindow 直接讀這個屬性),不用等按「儲存設定」才套用——
    /// 不過要跨次啟動記住,還是要按儲存寫進 config.json。</summary>
    [ObservableProperty] private bool _minimizeToTray;

    public SettingsViewModel(string? configPath = null)
    {
        _configPath = configPath ?? ConfigStore.DefaultPath;
        _config = ConfigStore.Load(_configPath);

        _connectionKind = IsConsideredProxyMode(_config.Default.Mode) ? ProxyLabel : DirectLabel;
        _proxySourceKind = _config.Default.ProxySourceKind == "configFile" ? ConfigFileLabel : SubscriptionLabel;
        _proxySubscriptionUrl = _config.Default.ProxySubscriptionUrl ?? "";
        _proxyConfigFilePath = _config.Default.ProxyConfigFilePath ?? "";
        _rdpCamouflageWhenDirect = _config.Default.RdpCamouflageWhenDirect;
        _fps = _config.Quality.Fps;
        _bitrateKbps = _config.Quality.BitrateKbps;
        _minimizeToTray = _config.MinimizeToTray;
    }

    private static bool IsConsideredProxyMode(string mode) => mode is "tcp" or "ws";

    [RelayCommand]
    private async Task BrowseConfigFileAsync()
    {
        var path = await FilePickerService.PickFileAsync("選擇代理設定檔");
        if (path is not null) ProxyConfigFilePath = path;
    }

    [RelayCommand]
    private void Save()
    {
        _config.Default.Mode = ConnectionKind == ProxyLabel ? "tcp" : "auto";
        _config.Default.ProxySourceKind = ProxySourceKind == ConfigFileLabel ? "configFile" : "subscription";
        _config.Default.ProxySubscriptionUrl = string.IsNullOrWhiteSpace(ProxySubscriptionUrl) ? null : ProxySubscriptionUrl.Trim();
        _config.Default.ProxyConfigFilePath = string.IsNullOrWhiteSpace(ProxyConfigFilePath) ? null : ProxyConfigFilePath.Trim();
        _config.Default.RdpCamouflageWhenDirect = RdpCamouflageWhenDirect;
        _config.Quality.Fps = Fps; // 不合法的值這裡就會丟例外,存檔前先擋下來
        _config.Quality.BitrateKbps = BitrateKbps;
        _config.MinimizeToTray = MinimizeToTray;

        ConfigStore.Save(_config, _configPath);
        SavedMessage = IsProxyMode
            ? "設定已儲存(代理節點解析還沒做,目前只是先存起來——見 docs/PROTOCOL.md)"
            : "設定已儲存";
    }
}
