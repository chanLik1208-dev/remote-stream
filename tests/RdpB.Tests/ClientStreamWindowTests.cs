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

    [Fact]
    public async Task ConnectAsync_PreMatchedCorrectFingerprint_StillAsksPinButStrictlyValidatesCertificate()
    {
        await using var host = new HostListenerService(_hostConfigPath);
        host.Start(port: 0);

        // 「允許預匹配模式」:指紋先知道(來自 Host.FingerprintHex,模擬使用者自己信任的
        // 管道事先拿到的值),PinPairingPending=true——Host 還不認得這個 Client,
        // PIN 還是要走一次。
        var record = new PinnedHostRecord
        {
            Name = "預匹配測試主機",
            HostAddress = $"127.0.0.1:{host.Port}",
            FingerprintHex = host.FingerprintHex!,
            PinPairingPending = true,
        };
        var client = new ClientConnectionService(_clientConfigPath);

        int pinRequested = 0;
        var result = await client.ConnectAsync(record, () =>
        {
            pinRequested++;
            return Task.FromResult<string?>(host.Pin);
        });

        try
        {
            Assert.Equal(1, pinRequested); // 預匹配不等於跳過配對,還是要問一次 PIN
            Assert.Equal(ConnectOutcome.Success, result.Outcome);
            Assert.False(record.PinPairingPending); // 配對做完之後,下次連線不用再問 PIN
            Assert.Equal(host.FingerprintHex, record.FingerprintHex);
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
