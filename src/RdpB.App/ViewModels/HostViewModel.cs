using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RdpB.App.Services;

namespace RdpB.App.ViewModels;

/// <summary>計畫 §8「本機當 Host」面板:一個開關啟動/停止,畫面上顯示 PIN、監聽埠、
/// 目前連進來的 Client 數。邏輯都在 <see cref="HostListenerService"/>,這裡只是把它
/// 轉成 Avalonia 綁得到的屬性。</summary>
public sealed partial class HostViewModel : ObservableObject
{
    private readonly HostListenerService _service;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _pin;
    [ObservableProperty] private int _port;
    [ObservableProperty] private int _connectedClientCount;

    public HostViewModel(HostListenerService? service = null)
    {
        _service = service ?? new HostListenerService();
        // HostListenerService.StateChanged 可能從背景的 accept loop 執行緒觸發(code review
        // 抓到的真坑:有人連上來那一刻就是背景執行緒),Avalonia 的綁定屬性只能在 UI 執行緒上
        // 改,所以一律 Dispatcher.UIThread.Post——即使剛好已經在 UI 執行緒上呼叫也是安全的,
        // 只是排一次隊,不會出錯。
        _service.StateChanged += () => Dispatcher.UIThread.Post(OnServiceStateChanged);
    }

    private void OnServiceStateChanged()
    {
        IsRunning = _service.IsRunning;
        Pin = _service.Pin;
        Port = _service.Port;
        ConnectedClientCount = _service.ConnectedClientCount;
    }

    [RelayCommand]
    private void ToggleHost()
    {
        if (_service.IsRunning)
            _ = StopAsync();
        else
            Start();
    }

    public void Start(int port = Core.Transport.DefaultPorts.Host)
    {
        _service.Start(port);
        OnServiceStateChanged();
    }

    public async Task StopAsync()
    {
        await _service.StopAsync();
        OnServiceStateChanged();
    }
}
