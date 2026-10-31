using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RdpB.App.Services;
using RdpB.App.Views;
using RdpB.Core.Transport;
using RdpB.Core.Windows;

namespace RdpB.App.ViewModels;

/// <summary>
/// 計畫 §8 主畫面的 ViewModel:裝置清單的新增/移除/連線測試,邏輯跟 Avalonia 完全分開,
/// 不用開真的視窗就能寫單元測試(§8「GUI:ViewModel 寫單元測試」)。
///
/// 目前「連線」按鈕只做到 <see cref="ConnectivityChecker"/> 的健檢(連得到、量多久),
/// 還沒真正開出串流視窗——那需要配對流程(計畫 §2)先串起來,指紋驗證才有意義,
/// 不然現在接受任何憑證,不是真正安全的連線。這個落差在這裡老實標出來。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly string _configPath;
    private RdpBConfig _config;

    public ObservableCollection<DeviceCardViewModel> Devices { get; } = new();

    /// <summary>v1 先用同一個視窗的分頁放設定(計畫 §8 的線框圖是獨立設定頁,這裡簡化成
    /// Tab 切換,邏輯不變,之後要拆成獨立視窗只是換個 View,ViewModel 不用動)。</summary>
    public SettingsViewModel Settings { get; }

    public HostViewModel Host { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    [ObservableProperty]
    private string _newDeviceName = "";

    [ObservableProperty]
    private string _newDeviceAddress = "";

    /// <summary>選填——「允許預匹配模式」:使用者透過自己信任的管道(讀 Host 螢幕上顯示的
    /// 指紋、電話告知、USB 傳遞……)事先拿到 Host 的憑證指紋,先填在這裡,連線時就不會
    /// TOFU 先接受再談 PIN,第一次連線就嚴格比對——見 <see cref="AddDevice"/> 和
    /// <see cref="ClientConnectionService.ConnectAsync"/> 上的註解。留空就是原本的行為。</summary>
    [ObservableProperty]
    private string _newDeviceFingerprintHex = "";

    [ObservableProperty]
    private string? _statusMessage;

    public MainWindowViewModel(string? configPath = null)
    {
        _configPath = configPath ?? ConfigStore.DefaultPath;
        _config = ConfigStore.Load(_configPath);
        foreach (var record in _config.PinnedHosts)
            Devices.Add(CreateCard(record));

        Settings = new SettingsViewModel(_configPath);

        // 真的坑(不是順手發現就隨便改掉的):這個屬性原本是 `= new()` 的欄位初始化式,
        // 完全沒帶 _configPath——HostListenerService 內部會各自掉回
        // ConfigStore.DefaultPath,導致 Host 分頁(配對紀錄、frp 設定)永遠讀寫真正的那份
        // config.json,不管呼叫端傳進來的 configPath 是什麼。無痕模式能不能真的「不留痕跡」
        // 直接依賴這裡接得對不對——這個修法不是無痕模式專屬的,任何用自訂 configPath 建構
        // MainWindowViewModel 的呼叫端(例如測試)都受影響。
        Host = new HostViewModel(new HostListenerService(_configPath), _configPath);
        Diagnostics = new DiagnosticsViewModel(_configPath);
    }

    private DeviceCardViewModel CreateCard(PinnedHostRecord record) =>
        new(record, TestConnectivityAsync, RemoveDevice, ConnectAsync, EditDeviceAsync);

    public void Reload()
    {
        _config = ConfigStore.Load(_configPath);
        Devices.Clear();
        foreach (var record in _config.PinnedHosts)
            Devices.Add(CreateCard(record));
    }

    [RelayCommand]
    private void AddDevice()
    {
        var address = NewDeviceAddress.Trim();
        if (address.Length == 0)
        {
            StatusMessage = "位址不能是空的";
            return;
        }

        var name = NewDeviceName.Trim();
        if (name.Length == 0) name = address;

        if (_config.PinnedHosts.Any(h => h.HostAddress == address))
        {
            StatusMessage = $"{address} 已經在清單裡了";
            return;
        }

        // 預匹配指紋(選填):使用者從自己信任的管道事先拿到的 Host 憑證指紋。SHA-256 雜湊
        // 固定 32 bytes,Convert.ToHexString 輸出固定 64 個十六進位字元——先做格式檢查,
        // 打錯字元或長度不對現在就講清楚,不要讓使用者等到連線失敗才一頭霧水不知道哪裡錯。
        string fingerprintInput = NewDeviceFingerprintHex.Trim().Replace(" ", "").Replace(":", "").ToUpperInvariant();
        if (fingerprintInput.Length > 0)
        {
            if (fingerprintInput.Length != 64 || !fingerprintInput.All(Uri.IsHexDigit))
            {
                StatusMessage = "指紋格式不對——應該是 64 個十六進位字元(SHA-256),可以帶冒號或空白分隔,會自動去掉";
                return;
            }
        }

        // PinPairingPending 只有「有預匹配指紋,但還沒真的配對過」這個情況才要設成 true——
        // 沒填指紋的一般新裝置,PinPairingPending 預設 false 完全沒問題(needsPinExchange
        // 本來就會因為 FingerprintHex 是空字串而要求配對,見 ClientConnectionService 上的註解)。
        var record = new PinnedHostRecord
        {
            Name = name,
            HostAddress = address,
            FingerprintHex = fingerprintInput,
            PinPairingPending = fingerprintInput.Length > 0,
        };
        _config.PinnedHosts.Add(record);
        ConfigStore.Save(_config, _configPath);

        Devices.Add(CreateCard(record));
        NewDeviceName = "";
        NewDeviceAddress = "";
        NewDeviceFingerprintHex = "";
        StatusMessage = fingerprintInput.Length > 0
            ? $"已新增 {name}(預匹配指紋,第一次連線會嚴格比對,仍需完成一次 PIN 配對)"
            : $"已新增 {name}(尚未配對)";
    }

    /// <summary>傳給 <see cref="DeviceCardViewModel"/> 的 callback,不是 XAML 指令——
    /// 卡片自己的「移除」按鈕綁的是它自己的 RemoveCommand,這裡只是實際邏輯。</summary>
    private void RemoveDevice(DeviceCardViewModel device)
    {
        _config.PinnedHosts.Remove(device.Record);
        ConfigStore.Save(_config, _configPath);
        Devices.Remove(device);
        StatusMessage = $"已移除 {device.Name}";
    }

    /// <summary>傳給卡片「連線」按鈕的 callback——真正把配對/串流接起來那條路,跟上面
    /// <see cref="TestConnectivityAsync"/> 只是健檢不一樣。邏輯都在
    /// <see cref="ClientConnectionService"/>,這裡只負責問 PIN(透過 <see cref="PinDialogService"/>,
    /// 不讓 ViewModel 直接碰 Window)和把結果反映到 StatusMessage。</summary>
    private async Task ConnectAsync(DeviceCardViewModel device)
    {
        StatusMessage = $"正在連線到 {device.Name}...";
        var service = new ClientConnectionService(_configPath);

        var result = await service.ConnectAsync(device.Record,
            () => PinDialogService.RequestPinAsync(device.Name),
            () => PinDialogService.RequestConnectionPasswordAsync(device.Name));

        StatusMessage = result.Message;
        if (result.Outcome == ConnectOutcome.Success)
        {
            // 配對剛剛才成功的話,FingerprintHex 會是新填上去的——卡片上雖然沒有直接顯示
            // 指紋,但重新整理一次清單確保 LastConnected 等欄位跟著畫面同步。
            Reload();
        }
    }

    /// <summary>齒輪按鈕的 callback——開 <see cref="EditHostDialog"/> 改名稱/位址/指紋/
    /// 連線覆寫,直接改 <see cref="DeviceCardViewModel.Record"/> 本身。對話框按「儲存」
    /// 才會回傳 true,這裡才需要真的存檔;按「取消」或關視窗什麼都不用做,record 本來就
    /// 沒被動過。跟 <see cref="PinDialogService"/> 共用同一個 Owner(App.axaml.cs 建好
    /// 主視窗後塞進去的那個),不讓 ViewModel 自己另外持有 Window 參考。</summary>
    private async Task EditDeviceAsync(DeviceCardViewModel device)
    {
        if (PinDialogService.Owner is null) return;

        var dialog = new EditHostDialog(device.Record);
        var saved = await dialog.ShowDialog<bool>(PinDialogService.Owner).ConfigureAwait(true);
        if (!saved) return;

        ConfigStore.Save(_config, _configPath);
        Reload();
        StatusMessage = $"已更新 {device.Record.Name}";
    }

    /// <summary>同樣是傳給卡片的 callback,不是 XAML 指令。</summary>
    private async Task TestConnectivityAsync(DeviceCardViewModel device)
    {
        StatusMessage = $"正在測試 {device.Name} 的連線...";
        var proxy = _config.Default.ProxyUrl is { Length: > 0 } ? ParseProxy(_config.Default.ProxyUrl) : null;
        var (host, port) = ParseHostAndPort(device.HostAddress);
        // QUIC/TCP 目前對 Host 來說是同一個埠(Host 只開一個 TCP+TLS 監聽,見
        // DefaultPorts 上的註解)——之前這裡誤寫成 7443/3389 兩個不相關的數字,
        // 3389 剛好是標準 RDP 埠,連線測試可能誤判連到對方機器「真的」Windows RDP 服務。
        var result = await ConnectivityChecker.CheckAsync(host, port, port, proxy);

        device.IsOnline = result.Success;
        device.LatencyMs = result.Success ? (int)result.Elapsed.TotalMilliseconds : null;
        StatusMessage = $"{device.Name}:{result.Message}";
    }

    /// <summary>位址欄位允許使用者打「192.168.0.120」或「192.168.0.120:7443」兩種格式——
    /// 實測發現打了埠號反而會把整串「host:port」當成主機名去解析,連不出去(見 UX 回饋)。
    /// 沒帶埠號就用 Host 的預設埠。
    ///
    /// code review 抓到的真坑:裸 IPv6(例如「fe80::1」)本身就含多個冒號,原本只看
    /// 「最後一個冒號」來切 host:port,會把它誤切成 host=`fe80:` port=`1`,整段位址
    /// 直接解析失敗。IPv6 + 埠號要用方括號(`[fe80::1]:7443`,RFC 3986 的寫法),這裡
    /// 照這個規則判斷,裸 IPv6 整串當成主機名、不嘗試拆埠號。</summary>
    public static (string Host, int Port) ParseHostAndPort(string address)
    {
        var trimmed = address.Trim();

        if (trimmed.StartsWith('['))
        {
            var closeIdx = trimmed.IndexOf(']');
            if (closeIdx > 0)
            {
                var bracketedHost = trimmed[1..closeIdx];
                var afterBracket = trimmed[(closeIdx + 1)..];
                if (afterBracket.StartsWith(':') && int.TryParse(afterBracket[1..], out var bracketedPort))
                    return (bracketedHost, bracketedPort);

                return (bracketedHost, DefaultPorts.Host); // "[::1]" 沒帶埠號
            }
        }

        // 裸位址裡出現兩個以上的冒號,幾乎可以斷定是沒加方括號的 IPv6 字面值
        // (例如「fe80::1」),不當成 host:port 去拆,整串當主機名。
        if (trimmed.Count(c => c == ':') > 1)
            return (trimmed, DefaultPorts.Host);

        var idx = trimmed.LastIndexOf(':');
        if (idx > 0 && idx < trimmed.Length - 1 && int.TryParse(trimmed[(idx + 1)..], out var port))
            return (trimmed[..idx], port);

        return (trimmed, DefaultPorts.Host);
    }

    private static ProxyOptions? ParseProxy(string url)
    {
        // 先支援最常見的 socks5://host:port 形式,帳密/HTTP CONNECT 等設定頁做出來後再補。
        if (!url.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url["socks5://".Length..];
        var parts = rest.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) return null;
        return new ProxyOptions(ProxyKind.Socks5, parts[0], port);
    }
}
