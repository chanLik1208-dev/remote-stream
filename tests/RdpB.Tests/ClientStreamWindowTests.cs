using RdpB.App.Services;
using RdpB.Core.Windows;

namespace RdpB.Tests;

/// <summary>端對端整合測試:真正的 Client 端串流視窗——不是丟棄的 probe,是 Kyra 實際會用的
/// 那條路(見 RdpB.Client.StreamWindow + RdpB.App.Services.ClientConnectionService)。
///
/// 連線 → mTLS → 配對(PIN 來自 Host 真的產生的那個,不是假的)→ Host 真的開始串流 →
/// Client 真的開出 Win32 視窗、Decoder 真的解出畫面、AudioPlayer 真的開始播放。這台機器
/// 有 GPU/互動桌面才跑得動,跟其他 Hardware 測試一樣。</summary>
[Trait("Category", "Hardware")]
public class ClientStreamWindowTests : IDisposable
{
    private readonly string _hostConfigPath = Path.Combine(Path.GetTempPath(), $"rdpb-streamwindow-host-{Guid.NewGuid():N}.json");
    private readonly string _clientConfigPath = Path.Combine(Path.GetTempPath(), $"rdpb-streamwindow-client-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task ConnectAsync_FirstTimePairing_OpensRealStreamWindowAndStartsStreaming()
    {
        await using var host = new HostListenerService(_hostConfigPath);
        host.Start(port: 0);

        var record = new PinnedHostRecord { Name = "整合測試用主機", HostAddress = $"127.0.0.1:{host.Port}", FingerprintHex = "" };
        var client = new ClientConnectionService(_clientConfigPath);

        var result = await client.ConnectAsync(record, () => Task.FromResult<string?>(host.Pin));

        try
        {
            Assert.Equal(ConnectOutcome.Success, result.Outcome);
            Assert.NotNull(result.Window);

            // 配對成功後 FingerprintHex 應該被填上去了,之後同一台裝置不用再輸入 PIN。
            Assert.NotEmpty(record.FingerprintHex);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!host.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.True(host.IsStreaming);

            // config.json 也該確實存下配對紀錄,不只是這次跑在記憶體裡那個 record 物件。
            var savedConfig = ConfigStore.Load(_clientConfigPath);
            Assert.Contains(savedConfig.PinnedHosts, h => h.HostAddress == record.HostAddress && h.FingerprintHex == record.FingerprintHex);
        }
        finally
        {
            result.Window?.Dispose();
        }
    }

    public void Dispose()
    {
        if (File.Exists(_hostConfigPath)) File.Delete(_hostConfigPath);
        if (File.Exists(_clientConfigPath)) File.Delete(_clientConfigPath);
    }
}
