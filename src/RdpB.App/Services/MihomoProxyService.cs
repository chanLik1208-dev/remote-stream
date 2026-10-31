using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RdpB.Core.Transport;

namespace RdpB.App.Services;

/// <summary>
/// 管理一個 mihomo 子行程的生命週期——Kyra 的訂閱裡有 RdpB 自己不會講的協定(見
/// <see cref="MihomoRuntime"/> 類別上的註解),把訂閱連結交給 mihomo 去抓/解析/連線,
/// mihomo 對外開一個本機 mixed-port(同時接受 HTTP/SOCKS5),RdpB 只要把
/// <see cref="RdpB.Core.Windows.TransportSettings.ProxyUrl"/> 指到這個本機 port 就好。
///
/// **這是 Client 端用的服務**,跟 <see cref="FrpTunnelService"/>(Host 端把自己曝露出去)
/// 方向相反——這裡是「這台機器要連出去,但中間要先繞過一個本機的協定轉換層」。
///
/// **關閉行程不用擔心撞名/撞 port 那類坑**(跟 <see cref="FrpTunnelService.KillCurrentProcess"/>
/// 上的故事不一樣):mihomo 不像 frpc 要去跟遠端伺服器登記一個共享的資源(proxy name/
/// remotePort),它只是本機單純跑著、監聽一個只有 127.0.0.1 連得到的 port,直接強制 Kill
/// 完全安全,不會留下任何需要被遠端清理的殘留狀態。
/// </summary>
public sealed class MihomoProxyService : IAsyncDisposable
{
    private readonly string _configDir;
    private readonly object _lock = new();
    private Process? _process;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public int MixedPort { get; private set; } = DefaultPorts.Mihomo;
    public int ControllerPort { get; private set; } = DefaultPorts.MihomoController;

    /// <summary>手動選節點(Kyra 要求的功能:自動 url-test 選最快的之外,也要能自己挑)用的
    /// select 群組名稱——規則(`MATCH,{SelectGroupName}`)指到這個群組,它的選項是「auto」
    /// (url-test 子群組,維持自動挑最快這個預設行為)加上訂閱裡每一個節點本身,見
    /// <see cref="BuildConfigYaml"/>。</summary>
    private const string SelectGroupName = "proxy";
    public const string AutoNodeLabel = "auto(自動選最快)";

    public event Action? StateChanged;

    public MihomoProxyService(string? configDir = null) =>
        _configDir = configDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB", "mihomo");

    /// <summary>啟動(或重啟)mihomo,指向 <paramref name="subscriptionUrl"/> 這份訂閱。
    /// 每次呼叫都會停掉舊的(如果有在跑)再開一個新的——訂閱網址換了、或要重新整理節點清單
    /// 時,呼叫端直接再呼叫一次這個方法即可,不用自己先手動 Stop。</summary>
    public async Task<bool> StartAsync(string subscriptionUrl, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(subscriptionUrl))
        {
            LastError = "訂閱連結是空的";
            StateChanged?.Invoke();
            return false;
        }

        string exePath;
        try
        {
            exePath = await MihomoRuntime.EnsureAvailableAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = $"mihomo.exe 準備失敗:{ex.Message}";
            StateChanged?.Invoke();
            return false;
        }

        Directory.CreateDirectory(_configDir);
        var configPath = Path.Combine(_configDir, "config.yaml");
        File.WriteAllText(configPath, BuildConfigYaml(subscriptionUrl, MixedPort, ControllerPort));

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"-d \"{_configDir}\" -f \"{configPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _configDir,
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 回傳 null");
        }
        catch (Exception ex)
        {
            LastError = $"啟動 mihomo.exe 失敗:{ex.Message}";
            StateChanged?.Invoke();
            return false;
        }

        // 跟 FrpTunnelService 同一個理由(見 ChildProcessJobTracker 類別上的註解):RdpB.exe
        // 被異常結束時,子行程本來不會自動死掉——這裡指派進同一個 Job,保證不會留下孤兒
        // mihomo.exe 繼續佔用 mixed-port/繼續連訂閱伺服器。
        ChildProcessJobTracker.Track(process);

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) RdpB.Core.Diagnostics.AppLog.WriteLine($"[mihomo] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) RdpB.Core.Diagnostics.AppLog.WriteLine($"[mihomo:stderr] {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_lock) { _process = process; }
        IsRunning = true;
        LastError = null;
        StateChanged?.Invoke();

        // 不像 frpc 那樣有「對外登記成不成功」的中間狀態要等——給它一點點時間真的把 mixed-port
        // 開起來,之後呼叫端(SettingsViewModel)可以直接去 connect 這個 port 驗證。給太短
        // 的話剛下載完第一次啟動(要解壓/初始化)可能還沒真的 ready。
        await Task.Delay(800, ct).ConfigureAwait(false);

        if (process.HasExited)
        {
            LastError = $"mihomo.exe 啟動後立刻結束(結束代碼 {process.ExitCode})——訂閱連結或設定可能有問題,看診斷頁的 log 檢視。";
            IsRunning = false;
            StateChanged?.Invoke();
            return false;
        }

        return true;
    }

    public Task StopAsync()
    {
        Process? p;
        lock (_lock) { p = _process; _process = null; }

        if (p is not null)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 可能已經自己結束了 */ }
            p.Dispose();
        }

        bool wasRunning = IsRunning;
        IsRunning = false;
        if (wasRunning) StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>最小可用設定:`mode: rule` 搭配單一一條規則(全部流量走 <see cref="SelectGroupName"/>
    /// 這個 select 群組)——不帶任何 geoip/geosite 規則,不需要額外下載 GeoIP/GeoSite 資料庫
    /// (mihomo 預設行為),保持啟動快、依賴少。
    ///
    /// **自動選 + 手動選並存**(Kyra 要求):`proxy` 是 `select` 類型,`use: [sub]` 讓訂閱裡
    /// 每個節點都變成一個可以直接選的選項,另外多塞一個 `auto`(`url-test` 子群組,一樣
    /// `use: [sub]`)當作「不想自己選就用這個」的預設值——`proxies: [auto]` 讓 `auto` 這個
    /// 群組名稱本身也出現在 `proxy` 的選項清單最前面。`external-controller` 開在 127.0.0.1
    /// (見 <see cref="DefaultPorts.MihomoController"/> 上的註解),給 <see cref="GetNodesAsync"/>/
    /// <see cref="SelectNodeAsync"/> 查詢/切換用。</summary>
    private static string BuildConfigYaml(string subscriptionUrl, int mixedPort, int controllerPort) =>
        $"""
        mixed-port: {mixedPort}
        allow-lan: false
        mode: rule
        log-level: info
        external-controller: 127.0.0.1:{controllerPort}

        proxy-providers:
          sub:
            type: http
            url: "{EscapeYamlString(subscriptionUrl)}"
            path: ./proxy_providers/sub.yaml
            interval: 3600
            health-check:
              enable: true
              url: https://www.gstatic.com/generate_204
              interval: 300
              timeout: 5000
              lazy: true

        proxy-groups:
          - name: {SelectGroupName}
            type: select
            proxies:
              - auto
            use:
              - sub
          - name: auto
            type: url-test
            use:
              - sub
            url: https://www.gstatic.com/generate_204
            interval: 300

        rules:
          - MATCH,{SelectGroupName}
        """;

    private static string EscapeYamlString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>查詢 <see cref="SelectGroupName"/> 這個群組目前有哪些節點可以選、現在選的是哪個。
    /// 回傳的節點清單裡,"auto" 這個選項固定排第一個(代表「自動選最快」,不是訂閱裡真的叫
    /// 這個名字的節點)——呼叫端(SettingsViewModel)不用自己特判。</summary>
    public async Task<(List<string> Nodes, string? Current)> GetGroupAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ControllerPort}"), Timeout = TimeSpan.FromSeconds(5) };
        var group = await http.GetFromJsonAsync<MihomoGroup>($"/proxies/{Uri.EscapeDataString(SelectGroupName)}", ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("mihomo controller 沒有回應預期的群組資料");

        var nodes = (group.All ?? []).Select(n => n == "auto" ? AutoNodeLabel : n).ToList();
        var current = group.Now == "auto" ? AutoNodeLabel : group.Now;
        return (nodes, current);
    }

    /// <summary>手動切換 <see cref="SelectGroupName"/> 要用哪個節點。<paramref name="nodeLabel"/>
    /// 是 <see cref="GetGroupAsync"/> 回傳的清單裡的其中一項(含 <see cref="AutoNodeLabel"/>
    /// 這個特殊選項)。</summary>
    public async Task SelectNodeAsync(string nodeLabel, CancellationToken ct = default)
    {
        string realName = nodeLabel == AutoNodeLabel ? "auto" : nodeLabel;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ControllerPort}"), Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.PutAsJsonAsync($"/proxies/{Uri.EscapeDataString(SelectGroupName)}", new { name = realName }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private sealed class MihomoGroup
    {
        [JsonPropertyName("now")] public string? Now { get; set; }
        [JsonPropertyName("all")] public List<string>? All { get; set; }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
