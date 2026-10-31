using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RdpB.App.Services;
using RdpB.Core.Transport;
using RdpB.Core.Windows;

namespace RdpB.App.ViewModels;

/// <summary>計畫 §8 診斷頁:輸入一個位址,依序測 QUIC 直連 → TCP 直連 → 代理,每一步
/// 顯示成功/失敗/原因;另外即時顯示 <see cref="RdpB.Core.Diagnostics.AppLog"/> 的內容
/// (log 檢視)。邏輯都在 <see cref="DiagnosticsService"/>,這裡只負責串 UI。</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly string _configPath;

    [ObservableProperty] private string _targetAddress = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<DiagnosticStepViewModel> Steps { get; } = new();

    /// <summary>倒序(最新的在最上面),這樣開著診斷頁盯著看的時候不用一直往下捲。</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    public DiagnosticsViewModel(string? configPath = null)
    {
        _configPath = configPath ?? ConfigStore.DefaultPath;

        foreach (var entry in RdpB.Core.Diagnostics.AppLog.Snapshot().Reverse())
            LogLines.Add(FormatLogEntry(entry));

        RdpB.Core.Diagnostics.AppLog.EntryAdded += OnLogEntryAdded;
    }

    private void OnLogEntryAdded(RdpB.Core.Diagnostics.AppLog.Entry entry) =>
        Dispatcher.UIThread.Post(() =>
        {
            LogLines.Insert(0, FormatLogEntry(entry));
            // 跟 AppLog 自己的環狀緩衝上限(500)同一個理由:UI 端的 ObservableCollection
            // 沒必要無限長大,Avalonia 的虛擬化清單在筆數太多時也會開始變慢。
            while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
        });

    private static string FormatLogEntry(RdpB.Core.Diagnostics.AppLog.Entry entry) =>
        $"[{entry.Time:HH:mm:ss}] {entry.Message}";

    [RelayCommand]
    private async Task RunAsync()
    {
        var address = TargetAddress.Trim();
        if (address.Length == 0)
        {
            StatusMessage = "位址不能是空的";
            return;
        }

        IsRunning = true;
        Steps.Clear();
        StatusMessage = $"正在測試 {address}...";

        try
        {
            var (host, port) = MainWindowViewModel.ParseHostAndPort(address);
            var config = ConfigStore.Load(_configPath);
            var proxy = config.Default.ProxyUrl is { Length: > 0 } ? ParseProxy(config.Default.ProxyUrl) : null;

            var results = await DiagnosticsService.RunAsync(host, port, port, proxy).ConfigureAwait(true);
            foreach (var r in results)
                Steps.Add(new DiagnosticStepViewModel(r));

            StatusMessage = $"{address} 測試完成";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static ProxyOptions? ParseProxy(string url)
    {
        if (!url.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url["socks5://".Length..];
        var parts = rest.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) return null;
        return new ProxyOptions(ProxyKind.Socks5, parts[0], port);
    }

    public void Dispose() => RdpB.Core.Diagnostics.AppLog.EntryAdded -= OnLogEntryAdded;
}

/// <summary>單一步驟的顯示包裝——把 DiagnosticsService.StepResult 轉成 XAML 容易綁的字串/
/// 顏色邏輯,跟 DeviceCardViewModel 同一個「卡片自己帶顯示邏輯」的模式。</summary>
public sealed class DiagnosticStepViewModel
{
    public string StepName { get; }
    public string StatusText { get; }
    public string Message { get; }
    public bool Success { get; }

    public DiagnosticStepViewModel(DiagnosticsService.StepResult result)
    {
        StepName = result.StepName;
        Message = result.Message;
        Success = result.Success;
        StatusText = !result.Attempted ? "○ 略過" : result.Success ? "✔ 成功" : "✘ 失敗";
    }
}
