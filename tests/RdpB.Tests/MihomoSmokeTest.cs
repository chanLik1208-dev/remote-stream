using RdpB.App.Services;
using Xunit;

namespace RdpB.Tests;

/// <summary>真的打 GitHub API 下載 mihomo.exe(61MB)、真的啟動一個子行程——跟其他
/// Hardware 分類的測試一樣,不跑在一般的 `dotnet test` 篩選範圍裡(太慢、碰網路,不適合
/// 當日常開發迴圈的一部分),但留著當 GitHub release 命名慣例/mihomo 設定格式變動時的
/// 早期警報,比等 Kyra 實際用到才發現壞掉好。</summary>
[Trait("Category", "Hardware")]
public class MihomoSmokeTest
{
    [Fact]
    public async Task DownloadAndStart_WithUnreachableSubscription_StillOpensMixedPort()
    {
        var exePath = await MihomoRuntime.EnsureAvailableAsync();
        Assert.True(File.Exists(exePath));

        await using var mihomo = new MihomoProxyService();
        // 故意用一個打不通的訂閱網址——驗證的重點是「mihomo 設定格式本身合法、
        // mixed-port 真的開得起來」,不是「訂閱抓得到」(那個需要真的訂閱,留給 Kyra 手動測)。
        bool started = await mihomo.StartAsync("https://example.com/fake-subscription-does-not-exist");

        Assert.True(started, mihomo.LastError);

        using var tcp = new System.Net.Sockets.TcpClient();
        var connectTask = tcp.ConnectAsync("127.0.0.1", mihomo.MixedPort);
        var completed = await Task.WhenAny(connectTask, Task.Delay(3000));
        Assert.True(completed == connectTask && tcp.Connected, "mixed-port 沒有真的開起來");
    }

    [Fact]
    public async Task ControllerApi_GetGroupAndSelectNode_RoundTrips()
    {
        await using var mihomo = new MihomoProxyService();
        bool started = await mihomo.StartAsync("https://example.com/fake-subscription-does-not-exist");
        Assert.True(started, mihomo.LastError);

        await Task.Delay(1500);

        var (nodes, _) = await mihomo.GetGroupAsync();
        Assert.Contains(MihomoProxyService.AutoNodeLabel, nodes);

        // 選回 auto 不該丟例外——這是 GetGroupAsync/SelectNodeAsync 對應到 mihomo 實際
        // RESTful API(GET/PUT /proxies/{group})的真實往返測試,不是 mock。
        await mihomo.SelectNodeAsync(MihomoProxyService.AutoNodeLabel);
    }
}
