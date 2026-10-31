using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>進階選項(預設關閉),見 ConfigStore.RdpCamouflageUseDedicatedPort 上的註解——
    /// Kyra 機器上真的撞過 3389 被 Windows 原生 Remote Desktop 服務佔用的坑,預設不佔用
    /// 專用埠,偽裝握手直接疊在原本要連的埠上。</summary>
    [ObservableProperty] private bool _rdpCamouflageUseDedicatedPort;

    /// <summary>Kyra 要求的開關:走 frp/DDNS 這類中間一定不轉發 UDP 的連線情境,QUIC 永遠連不上,
    /// 每次連線都白等 1.5 秒逾時才退回 TCP+TLS。見 ConfigStore.TransportSettings.DisableQuic
    /// 上的註解——跟選「透過代理」不一樣,這個開關不影響 §7 偽裝能不能用,單純跳過 QUIC 嘗試。</summary>
    [ObservableProperty] private bool _disableQuic;
    [ObservableProperty] private int _fps;
    [ObservableProperty] private int _bitrateKbps;

    /// <summary>Kyra 要求的畫質縮放(快速/平衡/品質/自訂),見 ConfigStore 的
    /// <see cref="RdpB.Core.Windows.QualityPresets"/>。選「自訂」以外的選項時,下面的
    /// Fps/BitrateKbps/ScalePercent 會被自動蓋成那組 preset 的數字——這三個欄位的
    /// NumericUpDown/ComboBox 在 GUI 端會依 <see cref="IsCustomPreset"/> 決定要不要讓使用者
    /// 手動改(不是這個 ViewModel 負責鎖,是 XAML 的 IsEnabled 綁定)。</summary>
    [ObservableProperty] private string _preset = QualityPresets.Balanced;

    [ObservableProperty] private int _scalePercent;
    [ObservableProperty] private string? _savedMessage;

    /// <summary>GUI 下拉選單顯示中文,存檔/邏輯用英文 key(<see cref="QualityPresets"/>)——
    /// 兩邊各自有各自的理由:存檔用英文 key 不會因為之後改文案措辭就讓舊 config.json 讀不出來,
    /// 下拉選單當然要中文。這個對照表只在 GUI 這一層,ConfigStore/CaptureEncoder 完全不知道
    /// 「快速/平衡/品質/自訂」這幾個字怎麼寫。</summary>
    private static readonly (string Key, string Label)[] PresetLabels =
    [
        (QualityPresets.Fast, "快速"),
        (QualityPresets.Balanced, "平衡"),
        (QualityPresets.Quality, "品質"),
        (QualityPresets.Custom, "自訂"),
    ];

    // code review 抓到的坑:原本這裡手動重複寫一次「快速/平衡/品質/自訂」,靠「順序要跟
    // PresetLabels 一樣」這個沒有編譯器幫忙檢查的默契維持同步——之後要是加一個新 preset
    // 或改個措辭,很容易只改到其中一份,下拉選單選出來的字跟 PresetLabel 查表對不起來。
    // 直接從 PresetLabels 衍生,只有一份真相。
    public IReadOnlyList<string> AvailablePresetLabels { get; } =
        Array.ConvertAll(PresetLabels, p => p.Label);

    public string PresetLabel
    {
        get
        {
            foreach (var p in PresetLabels) if (p.Key == Preset) return p.Label;
            return PresetLabels[1].Label; // 理論上不會發生(存檔的 key 壞掉才會到這裡),保底回「平衡」
        }
        set
        {
            foreach (var p in PresetLabels)
            {
                if (p.Label == value) { Preset = p.Key; return; }
            }
        }
    }

    public bool IsCustomPreset => Preset == QualityPresets.Custom;

    partial void OnPresetChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomPreset));
        OnPropertyChanged(nameof(PresetLabel));
        if (QualityPresets.TryGetValues(value, out var fps, out var bitrateKbps, out var scalePercent))
        {
            Fps = fps;
            BitrateKbps = bitrateKbps;
            ScalePercent = scalePercent;
        }
    }

    /// <summary>縮小/關閉視窗時自動收到系統匣(UX 回饋:自動化行為,不要另外放按鈕)。
    /// 這個開關即時生效(MainWindow 直接讀這個屬性),不用等按「儲存設定」才套用——
    /// 不過要跨次啟動記住,還是要按儲存寫進 config.json。</summary>
    [ObservableProperty] private bool _minimizeToTray;

    // ── 啟動模式(無痕模式,見 LaunchModeWindow/IncognitoSession)──────────────
    public string[] AvailableLaunchModes => [AskLabel, NormalLabel, IncognitoLabel];
    private const string AskLabel = "每次開啟都問";
    private const string NormalLabel = "一律一般模式";
    private const string IncognitoLabel = "一律無痕模式";

    [ObservableProperty] private string _launchMode;

    // ── 隱私防護(PrivacySettings,見 ConfigStore.cs 上的註解)──────────────────
    [ObservableProperty] private bool _blockScreenCapture;
    [ObservableProperty] private bool _detectTampering;

    // ── 左側分類導覽(Kyra 回饋:原本整頁用單一長條 StackPanel 把連線/畫質/一般/隱私
    // 全部疊在一起,每次加新功能就再往下疊一段,照計畫 §8 線框圖原本的設計改成「左側分類、
    // 右側內容」——不是美觀問題,是功能一直增加之後舊的平舖設計已經撐不住。這個 ViewModel
    // 本身的資料/邏輯完全沒變,只是多了「目前選哪個分類」這個純 UI 狀態。
    //
    // **基礎/進階 分頁**(Kyra 再次回饋:五個分類擠在同一層,連線/畫質/一般這種日常會動的
    // 設定跟隱私防護/HUD 這種「調過一次就不太會再碰」的進階選項混在一起同一層導覽)——
    // 分類本身不變,只是多包一層「屬於基礎還是進階」,左側分類清單依目前選的分頁動態篩選。</summary>
    private const string TransportCategory = "連線";
    private const string QualityCategory = "畫質";
    private const string GeneralCategory = "一般";
    private const string PrivacyCategory = "隱私防護";
    private const string HudCategory = "HUD";

    public string[] AvailableLevels => [BasicLevel, AdvancedLevel];
    private const string BasicLevel = "基礎";
    private const string AdvancedLevel = "進階";

    private static readonly string[] BasicCategories = [TransportCategory, QualityCategory, GeneralCategory];
    private static readonly string[] AdvancedCategories = [PrivacyCategory, HudCategory];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableCategories))]
    private string _selectedLevel = BasicLevel;

    public string[] AvailableCategories => SelectedLevel == AdvancedLevel ? AdvancedCategories : BasicCategories;

    partial void OnSelectedLevelChanged(string value)
    {
        // 切分頁時,如果目前選的分類不屬於新分頁,換成新分頁的第一個分類——
        // 不然右側內容會停在一個左側清單裡根本選不到的項目上。
        if (!AvailableCategories.Contains(SelectedCategory))
            SelectedCategory = AvailableCategories[0];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransportCategory))]
    [NotifyPropertyChangedFor(nameof(IsQualityCategory))]
    [NotifyPropertyChangedFor(nameof(IsGeneralCategory))]
    [NotifyPropertyChangedFor(nameof(IsPrivacyCategory))]
    [NotifyPropertyChangedFor(nameof(IsHudCategory))]
    private string _selectedCategory = TransportCategory;

    public bool IsTransportCategory => SelectedCategory == TransportCategory;
    public bool IsQualityCategory => SelectedCategory == QualityCategory;
    public bool IsGeneralCategory => SelectedCategory == GeneralCategory;
    public bool IsPrivacyCategory => SelectedCategory == PrivacyCategory;
    public bool IsHudCategory => SelectedCategory == HudCategory;

    // ── HUD(計畫 §4/§8,見 HudSettings 上的註解——F10 在視窗內三態循環,這裡是
    // 「開啟新串流視窗時的初始狀態」+位置/透明度/字級/更新頻率)──────────────
    public string[] AvailableHudPresets => [HudAllLabel, HudConnectionOnlyLabel, HudOffLabel];
    private const string HudAllLabel = "全部(連線狀況+協議細節)";
    private const string HudConnectionOnlyLabel = "只顯示連線狀況";
    private const string HudOffLabel = "全關";

    public string[] AvailableHudPositions => [HudTopLeftLabel, HudTopRightLabel, HudBottomLeftLabel, HudBottomRightLabel];
    private const string HudTopLeftLabel = "左上角";
    private const string HudTopRightLabel = "右上角";
    private const string HudBottomLeftLabel = "左下角";
    private const string HudBottomRightLabel = "右下角";

    [ObservableProperty] private string _hudPreset = HudConnectionOnlyLabel;
    [ObservableProperty] private string _hudPosition = HudTopLeftLabel;
    [ObservableProperty] private double _hudOpacity = 0.85;
    [ObservableProperty] private int _hudFontSize = 12;
    [ObservableProperty] private double _hudUpdateHz = 1.0;

    // ── 整份設定檔的密碼加密(Kyra 要求的功能,見 ConfigStore 上的註解)────────────
    // 原本設計成只擋「設定」這個分頁的 UI 畫面(存 PBKDF2 雜湊,config.json 本身還是明文)
    // ——Kyra 的回饋是「用密碼鎖保護的東西請直接加密」,改成整份 config.json 真的用密碼
    // 加密,解密只在程式啟動那一刻問一次(App.axaml.cs 的 EnsureConfigUnlockedAsync)。
    // 這裡不需要再另外做一層「這個分頁要不要先解鎖」的 UI 狀態——能走到這個 ViewModel 的
    // 建構子,就代表 config.json 已經在程式啟動時成功解密過了(不然 ConfigStore.Load 會
    // 直接丟例外,App.axaml.cs 根本不會走到建立這個 ViewModel 那一步)。
    [ObservableProperty] private bool _settingsLockEnabled;
    [ObservableProperty] private string _newLockPassword = ""; // 寫入專用,存檔後立刻清空,不回填明文
    [ObservableProperty] private string? _lockSavedMessage;

    public SettingsViewModel(string? configPath = null)
    {
        _configPath = configPath ?? ConfigStore.DefaultPath;
        _config = ConfigStore.Load(_configPath);

        _connectionKind = IsConsideredProxyMode(_config.Default.Mode) ? ProxyLabel : DirectLabel;
        _proxySourceKind = _config.Default.ProxySourceKind == "configFile" ? ConfigFileLabel : SubscriptionLabel;
        _proxySubscriptionUrl = _config.Default.ProxySubscriptionUrl ?? "";
        _proxyConfigFilePath = _config.Default.ProxyConfigFilePath ?? "";
        _rdpCamouflageWhenDirect = _config.Default.RdpCamouflageWhenDirect;
        _rdpCamouflageUseDedicatedPort = _config.Default.RdpCamouflageUseDedicatedPort;
        _disableQuic = _config.Default.DisableQuic;
        _fps = _config.Quality.Fps;
        _bitrateKbps = _config.Quality.BitrateKbps;
        _scalePercent = _config.Quality.ScalePercent;
        _preset = _config.Quality.Preset; // 用欄位(_preset)直接寫,不要用屬性(Preset)——
        // 屬性的 setter 會觸發 OnPresetChanged 把 Fps/BitrateKbps/ScalePercent 蓋成 preset
        // 預設值,載入既有設定檔時應該原封不動顯示存檔當下的數字,不該被「選到這個 preset」
        // 的副作用動到。
        _minimizeToTray = _config.MinimizeToTray;
        _launchMode = _config.DefaultLaunchMode switch
        {
            "normal" => NormalLabel,
            "incognito" => IncognitoLabel,
            _ => AskLabel,
        };
        _blockScreenCapture = _config.Privacy.BlockScreenCapture;
        _detectTampering = _config.Privacy.DetectTampering;

        _hudPreset = _config.Hud.Preset switch
        {
            "all" => HudAllLabel,
            "off" => HudOffLabel,
            _ => HudConnectionOnlyLabel,
        };
        _hudPosition = _config.Hud.Position switch
        {
            "top-right" => HudTopRightLabel,
            "bottom-left" => HudBottomLeftLabel,
            "bottom-right" => HudBottomRightLabel,
            _ => HudTopLeftLabel,
        };
        _hudOpacity = _config.Hud.Opacity;
        _hudFontSize = _config.Hud.FontSize;
        _hudUpdateHz = _config.Hud.UpdateHz;

        _settingsLockEnabled = ConfigStore.IsEncrypted(_configPath);
    }

    private static bool IsConsideredProxyMode(string mode) => mode is "tcp" or "ws";

    /// <summary>加密只對**真正的** config.json 開放,無痕模式(_configPath 是暫存複本)
    /// 不給選——code review 抓到的真坑:ConfigStore 快取的金鑰/salt 是整個行程共用的單一組
    /// 全域狀態,不是每個 path 各自一份。如果在無痕模式裡對暫存複本呼叫
    /// EnableEncryption/DisableEncryption,會把這個行程的金鑰整個換掉,後續任何讀寫真正
    /// config.json 的地方(例如「記住啟動模式」要寫回真正的檔案)都會因為金鑰對不上而噴
    /// InvalidOperationException。暫存複本本來就是「關掉就消失」,加密它也沒有意義,乾脆
    /// 在 UI 層就不給選,從根本上避開這個情境,而不是在 ConfigStore 那層做更複雜的
    /// per-path 金鑰管理去硬撐一個沒有實際用途的功能組合。</summary>
    public bool IsEncryptionAvailable => string.Equals(_configPath, ConfigStore.DefaultPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>加密開關本身——跟下面的 Save()(連線/畫質/一般/隱私/HUD)分開存,開/關
    /// 加密不需要連帶把其他分類的改動一起存下去,反之亦然。真的呼叫
    /// <see cref="ConfigStore.EnableEncryption"/>/<see cref="ConfigStore.DisableEncryption"/>
    /// 把整份 config.json 重新存成加密/明文格式,不是存一個「有沒有鎖」的旗標而已。</summary>
    [RelayCommand]
    private void SaveLockSettings()
    {
        if (!IsEncryptionAvailable)
        {
            LockSavedMessage = "無痕模式不支援這個功能(暫存設定複本關掉就消失,加密沒有意義)。";
            return;
        }

        bool currentlyEncrypted = ConfigStore.IsEncrypted(_configPath);

        if (SettingsLockEnabled)
        {
            if (NewLockPassword.Length == 0)
            {
                LockSavedMessage = currentlyEncrypted
                    ? "已經是加密狀態——留空密碼代表不變更,沒有東西要存"
                    : "要開啟加密,請先輸入一組密碼";
                return;
            }

            ConfigStore.EnableEncryption(NewLockPassword, _configPath);
            NewLockPassword = ""; // 存進去之後立刻清掉畫面上的欄位,不要讓明文一直留著
            LockSavedMessage = "設定檔已加密——下次啟動程式時需要輸入這組密碼。";
        }
        else if (!currentlyEncrypted)
        {
            // code review 抓到的坑:這個分支原本不管「本來就是明文」還是「真的剛解除加密」
            // 都一律顯示「已改回明文儲存」——使用者沒開過加密、只是碰了一下這個分類再按
            // 「套用」,會被誤導成剛剛發生了什麼變化,其實什麼都沒寫。
            LockSavedMessage = "目前就是明文儲存,沒有東西要存。";
        }
        else
        {
            // 走到這裡一定是 currentlyEncrypted == true(上面的 else if 已經擋掉 false 的情況)。
            ConfigStore.DisableEncryption(_configPath);
            NewLockPassword = "";
            LockSavedMessage = "已改回明文儲存。";
        }
    }

    [RelayCommand]
    private async Task BrowseConfigFileAsync()
    {
        var path = await FilePickerService.PickFileAsync("選擇代理設定檔");
        if (path is not null) ProxyConfigFilePath = path;
    }

    [ObservableProperty] private bool _isResolvingProxyNode;
    [ObservableProperty] private string? _proxyResolveMessage;

    /// <summary>Kyra 回饋:訂閱連結裡的節點多半是 Hysteria2/VLESS 這類 RdpB 自己不會講的
    /// 協定(見 <see cref="MihomoRuntime"/> 類別上的註解),純文字掃描 socks5/http 這招對
    /// 真實訂閱幾乎沒用。訂閱來源一律交給內嵌的 mihomo 處理;只有「匯入設定檔」這條路
    /// (使用者自己丟一個已經是 socks5/http 格式的檔案進來,比較簡單的情境)還是用
    /// <see cref="ProxyNodeResolver"/> 的純文字掃描。這個欄位整個 ViewModel 活著期間只會有
    /// 一個實例,"解析節點" 按下去成功之後,mihomo 子行程會繼續留著跑(真正連線時才用得到),
    /// 不會因為這個 ViewModel 被回收就跟著死掉——app 真正關閉時由 <see cref="StopMihomoAsync"/>
    /// 收掉,避免變成孤兒行程。</summary>
    private readonly MihomoProxyService _mihomo = new();

    /// <summary>Kyra 要求的功能:自動選最快的之外,也要能自己手動挑訂閱裡的某一個節點。
    /// 清單第一項固定是 <see cref="MihomoProxyService.AutoNodeLabel"/>(維持自動選的行為),
    /// 後面接著訂閱裡每一個節點的名稱。</summary>
    public ObservableCollection<string> AvailableMihomoNodes { get; } = new();

    [ObservableProperty] private string? _selectedMihomoNode;
    [ObservableProperty] private bool _isMihomoNodeListVisible;

    /// <summary>populate 清單時(從 mihomo controller 查回來的「目前選的是哪個」)要先設定
    /// 這個欄位,避免觸發 <see cref="OnSelectedMihomoNodeChanged"/> 又反過來呼叫一次
    /// SelectNodeAsync——那是使用者「自己動手選」才該做的事,不是我們把查到的現狀寫回
    /// UI 的時候。</summary>
    private bool _isPopulatingMihomoNodes;

    partial void OnSelectedMihomoNodeChanged(string? value)
    {
        if (_isPopulatingMihomoNodes || value is null) return;
        _ = ApplyMihomoNodeSelectionAsync(value);
    }

    private async Task ApplyMihomoNodeSelectionAsync(string nodeLabel)
    {
        try
        {
            await _mihomo.SelectNodeAsync(nodeLabel).ConfigureAwait(true);
            ProxyResolveMessage = $"已切換到節點:{nodeLabel}";
        }
        catch (Exception ex)
        {
            ProxyResolveMessage = $"切換節點失敗:{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ResolveProxyNodeAsync()
    {
        IsResolvingProxyNode = true;
        ProxyResolveMessage = "解析中…";
        try
        {
            if (IsConfigFileSource)
            {
                IsMihomoNodeListVisible = false;
                var result = ProxyNodeResolver.ResolveFromConfigFile(ProxyConfigFilePath);
                ProxyResolveMessage = result.Message;
                if (!result.Success || result.Node is null) return;

                _config.Default.ProxyUrl = $"{(result.Node.Kind == "http" ? "http" : "socks5")}://{result.Node.Host}:{result.Node.Port}";
                _config.Default.ProxyUsername = result.Node.Username;
                _config.Default.ProxyPassword = result.Node.Password; // 走 DPAPI(見屬性上的註解),不是明文存
                ConfigStore.Save(_config, _configPath);
                return;
            }

            ProxyResolveMessage = "正在準備 mihomo(第一次用要先下載,可能要一點時間)…";
            bool started = await _mihomo.StartAsync(ProxySubscriptionUrl).ConfigureAwait(true);
            if (!started)
            {
                ProxyResolveMessage = _mihomo.LastError ?? "mihomo 啟動失敗,原因不明";
                IsMihomoNodeListVisible = false;
                return;
            }

            _config.Default.ProxyUrl = $"socks5://127.0.0.1:{_mihomo.MixedPort}";
            _config.Default.ProxyUsername = null; // mihomo 自己的本機 mixed-port 不需要帳密
            _config.Default.ProxyPassword = null;
            ConfigStore.Save(_config, _configPath);
            ProxyResolveMessage = "mihomo 已啟動,正在抓節點清單…";

            await RefreshMihomoNodeListAsync().ConfigureAwait(true);
        }
        finally
        {
            IsResolvingProxyNode = false;
        }
    }

    /// <summary>剛啟動的 mihomo 要抓訂閱、解析完節點清單才查得到,不是啟動那一刻就立刻有——
    /// 用短輪詢等它準備好,而不是固定睡一個數字賭剛好夠長(訂閱大小/對方伺服器速度都會影響
    /// 要等多久)。查不到也不是致命錯誤,只是先不顯示手動選節點那塊 UI,使用者還是可以用
    /// 「auto」正常連線,之後再按「重新整理節點清單」補查一次。</summary>
    [RelayCommand]
    private async Task RefreshMihomoNodeListAsync()
    {
        const int maxAttempts = 10;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var (nodes, current) = await _mihomo.GetGroupAsync().ConfigureAwait(true);
                if (nodes.Count > 0)
                {
                    _isPopulatingMihomoNodes = true;
                    AvailableMihomoNodes.Clear();
                    foreach (var n in nodes) AvailableMihomoNodes.Add(n);
                    SelectedMihomoNode = current;
                    _isPopulatingMihomoNodes = false;

                    IsMihomoNodeListVisible = true;
                    ProxyResolveMessage = $"節點清單已更新,共 {nodes.Count} 個選項(含「{MihomoProxyService.AutoNodeLabel}」)。";
                    return;
                }
            }
            catch
            {
                // controller 可能還沒開起來、或 provider 還在抓——繼續重試,不當作錯誤。
            }
            await Task.Delay(1000).ConfigureAwait(true);
        }
        ProxyResolveMessage = "mihomo 已啟動,但節點清單還沒準備好(訂閱可能比較大或對方伺服器慢)——可以稍後再按「重新整理節點清單」。";
    }

    /// <summary>app 真正要關閉時呼叫(見 App.axaml.cs),避免 mihomo.exe 變成孤兒行程留著
    /// 繼續佔用 mixed-port/繼續連訂閱伺服器。</summary>
    public Task StopMihomoAsync() => _mihomo.StopAsync();

    [RelayCommand]
    private void Save()
    {
        _config.Default.Mode = ConnectionKind == ProxyLabel ? "tcp" : "auto";
        _config.Default.ProxySourceKind = ProxySourceKind == ConfigFileLabel ? "configFile" : "subscription";
        _config.Default.ProxySubscriptionUrl = string.IsNullOrWhiteSpace(ProxySubscriptionUrl) ? null : ProxySubscriptionUrl.Trim();
        _config.Default.ProxyConfigFilePath = string.IsNullOrWhiteSpace(ProxyConfigFilePath) ? null : ProxyConfigFilePath.Trim();
        _config.Default.RdpCamouflageWhenDirect = RdpCamouflageWhenDirect;
        _config.Default.RdpCamouflageUseDedicatedPort = RdpCamouflageUseDedicatedPort;
        _config.Default.DisableQuic = DisableQuic;
        _config.Quality.Fps = Fps; // 不合法的值這裡就會丟例外,存檔前先擋下來
        _config.Quality.BitrateKbps = BitrateKbps;
        _config.Quality.ScalePercent = ScalePercent;
        _config.Quality.Preset = Preset;
        _config.MinimizeToTray = MinimizeToTray;
        _config.DefaultLaunchMode = LaunchMode switch
        {
            NormalLabel => "normal",
            IncognitoLabel => "incognito",
            _ => "ask",
        };
        _config.Privacy.BlockScreenCapture = BlockScreenCapture;
        _config.Privacy.DetectTampering = DetectTampering;

        _config.Hud.Preset = HudPreset switch
        {
            var p when p == HudAllLabel => "all",
            var p when p == HudOffLabel => "off",
            _ => "connection-only",
        };
        _config.Hud.Position = HudPosition switch
        {
            var p when p == HudTopRightLabel => "top-right",
            var p when p == HudBottomLeftLabel => "bottom-left",
            var p when p == HudBottomRightLabel => "bottom-right",
            _ => "top-left",
        };
        _config.Hud.Opacity = Math.Clamp(HudOpacity, 0, 1);
        _config.Hud.FontSize = Math.Clamp(HudFontSize, 8, 32);
        _config.Hud.UpdateHz = Math.Clamp(HudUpdateHz, 0.1, 10.0);

        ConfigStore.Save(_config, _configPath);
        SavedMessage = IsProxyMode
            ? "設定已儲存(代理節點解析還沒做,目前只是先存起來——見 docs/PROTOCOL.md)"
            : "設定已儲存";
    }
}
