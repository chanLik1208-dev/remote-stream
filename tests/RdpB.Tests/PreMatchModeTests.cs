using RdpB.App.Services;
using RdpB.Core.Windows;

namespace RdpB.Tests;

/// <summary>「允許預匹配模式」關掉的是 TOFU 固有的破綻:使用者事先從自己信任的管道知道
/// Host 的指紋,第一次連線就該嚴格比對,不是先接受任何憑證再談 PIN。這個測試不需要真的
/// 開 GPU/串流——這裡測的是「錯的指紋連 PIN 都不會問就直接被擋」,連線失敗發生在真正
/// 開始配對之前,不會碰到 CaptureEncoder,所以不用標 Hardware。</summary>
public class PreMatchModeTests : IDisposable
{
    private readonly string _hostConfigPath = Path.Combine(Path.GetTempPath(), $"rdpb-prematch-host-{Guid.NewGuid():N}.json");
    private readonly string _clientConfigPath = Path.Combine(Path.GetTempPath(), $"rdpb-prematch-client-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task ConnectAsync_PreMatchedWrongFingerprint_FailsAtTlsLayer_NeverAsksForPin()
    {
        await using var host = new HostListenerService(_hostConfigPath);
        host.Start(port: 0);

        // 故意填一個跟 Host 真正指紋不一樣的值,模擬「中間人拿別的憑證冒充」或單純打錯——
        // 兩種情況都該在 TLS handshake 那一步就被擋下來,不該有任何機會走到談 PIN 這一步。
        var wrongFingerprint = new string('0', 64);
        Assert.NotEqual(wrongFingerprint, host.FingerprintHex);

        var record = new PinnedHostRecord
        {
            Name = "預匹配測試主機(錯誤指紋)",
            HostAddress = $"127.0.0.1:{host.Port}",
            FingerprintHex = wrongFingerprint,
            PinPairingPending = true,
        };
        var client = new ClientConnectionService(_clientConfigPath);

        bool pinRequested = false;
        var result = await client.ConnectAsync(record, () =>
        {
            pinRequested = true;
            return Task.FromResult<string?>("000000");
        });

        Assert.False(pinRequested); // 連線在 TLS 層就該失敗,根本不會走到問 PIN 這一步
        Assert.Equal(ConnectOutcome.ConnectFailed, result.Outcome);
        Assert.Null(result.Window);
        Assert.False(host.IsStreaming); // Host 端當然也沒有開始串流
    }

    public void Dispose()
    {
        if (File.Exists(_hostConfigPath)) File.Delete(_hostConfigPath);
        if (File.Exists(_clientConfigPath)) File.Delete(_clientConfigPath);
    }
}
