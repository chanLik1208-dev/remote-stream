using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RdpB.Core.Windows;

namespace RdpB.App.ViewModels;

/// <summary>
/// 計畫 §8 主畫面的一張裝置卡片,包住一筆 <see cref="PinnedHostRecord"/>。
///
/// **按鈕指令直接放在這個類別上**,不要在 XAML 裡用
/// <c>$parent[ItemsControl].((vm:MainWindowViewModel)DataContext).XxxCommand</c>
/// 這種「跨過 DataTemplate 去抓父層 ViewModel」的寫法——那是跑出一個真的 bug 才換掉的:
/// `dotnet run`(Debug、一般建置)下沒事,但 `dotnet publish -p:PublishSingleFile=true`
/// 的單一 exe 建置下,Avalonia 的執行期 XAML 型別解析(XamlIl 的 loose binding,帶
/// inline 型別轉型 `(vm:Type)` 那種)會直接丟 `ArgumentException: Unable to resolve type`,
/// 整個視窗一開就當掉——只有在真的打包後的 exe 才會炸,平常開發用 `dotnet run` 測不出來。
/// 卡片自己帶指令、透過建構子傳進來的 callback 呼叫回 MainWindowViewModel,完全不需要那段
/// 跨層 binding,XAML 只要 `{Binding RemoveCommand}` 這種最單純的寫法。
/// </summary>
public sealed partial class DeviceCardViewModel : ObservableObject
{
    private readonly Func<DeviceCardViewModel, Task> _onTestConnectivity;
    private readonly Action<DeviceCardViewModel> _onRemove;
    private readonly Func<DeviceCardViewModel, Task> _onConnect;

    public PinnedHostRecord Record { get; }

    public string Name => Record.Name;
    public string HostAddress => Record.HostAddress;

    public string LastConnectedText => Record.LastConnected == default
        ? "從未連線過"
        : $"上次連線:{Record.LastConnected.LocalDateTime:yyyy/MM/dd HH:mm}";

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private int? _latencyMs;

    /// <summary>連線中(mTLS handshake/配對 PIN 等待中)不讓使用者重複按「連線」——
    /// 避免同一台裝置同時打出兩條連線互相搶(Host 端現在雖然已經有 code review 修過的
    /// 第二連線搶位邏輯會擋掉,但在這裡先擋一次,使用者體感也比較不會覺得按了沒反應。</summary>
    [ObservableProperty]
    private bool _isConnecting;

    public string StatusText => IsOnline
        ? (LatencyMs is { } ms ? $"● 線上 {ms}ms" : "● 線上")
        : "○ 離線";

    public DeviceCardViewModel(
        PinnedHostRecord record,
        Func<DeviceCardViewModel, Task> onTestConnectivity,
        Action<DeviceCardViewModel> onRemove,
        Func<DeviceCardViewModel, Task> onConnect)
    {
        Record = record;
        _onTestConnectivity = onTestConnectivity;
        _onRemove = onRemove;
        _onConnect = onConnect;
    }

    [RelayCommand]
    private Task TestConnectivityAsync() => _onTestConnectivity(this);

    [RelayCommand]
    private void Remove() => _onRemove(this);

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnecting) return;
        IsConnecting = true;
        try { await _onConnect(this); }
        finally { IsConnecting = false; }
    }
}
