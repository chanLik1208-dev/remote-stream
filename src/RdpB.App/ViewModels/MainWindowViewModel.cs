using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RdpB.App.Services;
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

    public HostViewModel Host { get; } = new();

    [ObservableProperty]
    private string _newDeviceName = "";

    [ObservableProperty]
    private string _newDeviceAddress = "";

    [ObservableProperty]
    private string? _statusMessage;

    public MainWindowViewModel(string? configPath = null)
    {
        _configPath = configPath ?? ConfigStore.DefaultPath;
        _config = ConfigStore.Load(_configPath);
        foreach (var record in _config.PinnedHosts)
            Devices.Add(CreateCard(record));

        Settings = new SettingsViewModel(_configPath);
    }

    private DeviceCardViewModel CreateCard(PinnedHostRecord record) =>
        new(record, TestConnectivityAsync, RemoveDevice, ConnectAsync);

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

        // FingerprintHex 先留空——要真的配對過(計畫 §2)才會有憑證指紋可以填,
        // 這裡只是把裝置記下來,還不是「已配對、可信任」的狀態。
        var record = new PinnedHostRecord { Name = name, HostAddress = address, FingerprintHex = "" };
        _config.PinnedHosts.Add(record);
        ConfigStore.Save(_config, _configPath);

        Devices.Add(CreateCard(record));
        NewDeviceName = "";
        NewDeviceAddress = "";
        StatusMessage = $"已新增 {name}(尚未配對)";
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

        var result = await service.ConnectAsync(device.Record, () => PinDialogService.RequestPinAsync(device.Name));

        StatusMessage = result.Message;
        if (result.Outcome == ConnectOutcome.Success)
        {
            // 配對剛剛才成功的話,FingerprintHex 會是新填上去的——卡片上雖然沒有直接顯示
            // 指紋,但重新整理一次清單確保 LastConnected 等欄位跟著畫面同步。
            Reload();
        }
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
