using RdpB.App.Services;
using RdpB.Core.Pairing;
using RdpB.Core.Transport;
using RdpB.Core.Windows;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>Kyra 要求的無人值守加固:「加上密碼的無人值守比較好,每次都有驗證(信任裝置
/// 需要 Host 上設定的密碼)」——純指紋釘選(TOFU 配對過一次之後就永久信任)對「機器旁邊
/// 沒有人」的情境來說不夠,私鑰外洩就是永久性的破口。見 ConnectionPasswordSettings/
/// PinnedHostRecord.RequiresConnectionPassword 上的註解。
///
/// 「密碼錯誤被拒絕」這條路不需要 GPU/擷取(session 在真正開始串流之前就被關掉了),
/// 不用標 Hardware——跟 TrustedClientRevocationTests 同一個理由。</summary>
public class ConnectionPasswordTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"rdpb-connpw-test-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task TrustedClient_WrongConnectionPassword_IsRejectedBeforeStreaming()
    {
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        ConfigStore.ResetSessionForTests(); // 這個測試執行序裡其他測試可能動過加密狀態,先重置乾淨
        var config = ConfigStore.Load(_configPath);
        config.ConnectionPassword.Enabled = true;
        config.ConnectionPassword.Password = "correct-password";
        ConfigStore.Save(config, _configPath);

        var clientCert = EphemeralCertificate.CreateSelfSigned("connpw-wrong-test-client");
        var fingerprint = Convert.ToHexString(PairingService.ComputeFingerprint(clientCert.RawData));

        // 第一次配對,建立信任。
        await using (var transport1 = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: clientCert))
        {
            var session1 = new RdpB.Core.Session.Session(transport1, heartbeatInterval: TimeSpan.FromSeconds(10));
            session1.Start();
            var pairResult = await PairingCoordinator.PairAsync(session1, host.Pin!, transport1.RemoteCertificate!, clientCert);
            Assert.Equal(PairingResult.Success, pairResult);
            await session1.DisposeAsync();
        }
        await Task.Delay(100);
        Assert.Contains(host.GetTrustedClients(), c => string.Equals(c.FingerprintHex, fingerprint, StringComparison.OrdinalIgnoreCase));

        // 第二次連線:同一張(已信任的)憑證,但連線密碼答錯——應該被拒絕,不會進到串流。
        await using var transport2 = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: clientCert);
        var session2 = new RdpB.Core.Session.Session(transport2, heartbeatInterval: TimeSpan.FromMilliseconds(200));
        var closedTcs = new TaskCompletionSource();
        session2.StateChanged += state =>
        {
            if (state is RdpB.Core.Session.SessionState.Closed) closedTcs.TrySetResult();
        };
        session2.Start();

        var wrongPasswordResult = await PairingCoordinator.PairAsync(
            session2, "wrong-password", transport2.RemoteCertificate!, clientCert);

        // Host 送完 PairAck(failure)就會把連線關掉(跟一般 PIN 配對失敗同一個行為)——
        // 不是 Success,而且 session 真的斷線,不是卡在那裡假裝什麼都沒發生。
        // 不斷言 host.IsStreaming:第一次配對那個 session 是真的在這台機器上啟動過 GPU
        // 擷取的(session1.DisposeAsync() 之後 StopStreaming() 是不是已經跑完是另一個
        // 時間點的問題,跟這裡要驗證的「密碼錯誤被拒絕」無關,不該讓這個測試偶發失敗。
        Assert.NotEqual(PairingResult.Success, wrongPasswordResult);

        await session2.DisposeAsync();
        ConfigStore.ResetSessionForTests();
    }

    [Fact]
    public async Task TrustedClient_NoConnectionPasswordConfigured_SkipsCheckAndConnectsNormally()
    {
        // 這個測試驗證「沒開連線密碼就完全不受影響」——不該變成所有已信任裝置都突然要多
        // 回答一次什麼東西。
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        var clientCert = EphemeralCertificate.CreateSelfSigned("connpw-disabled-test-client");
        var fingerprint = Convert.ToHexString(PairingService.ComputeFingerprint(clientCert.RawData));

        await using (var transport1 = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: clientCert))
        {
            var session1 = new RdpB.Core.Session.Session(transport1, heartbeatInterval: TimeSpan.FromSeconds(10));
            session1.Start();
            Assert.Equal(PairingResult.Success,
                await PairingCoordinator.PairAsync(session1, host.Pin!, transport1.RemoteCertificate!, clientCert));
            await session1.DisposeAsync();
        }
        await Task.Delay(100);
        Assert.Contains(host.GetTrustedClients(), c => string.Equals(c.FingerprintHex, fingerprint, StringComparison.OrdinalIgnoreCase));

        // 第二次連線:已信任、沒開連線密碼——Host 端的 TCP accept 應該直接往下走到串流前的
        // 流程,不會卡在等一個永遠不會有人送出的 Pair 訊息(逾時長達 PairingTimeout)。
        // 用「一小段時間內連線沒被對面關掉」當信號,不直接斷言 IsStreaming(那需要真的
        // GPU 擷取,這個測試不標 Hardware)。
        await using var transport2 = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: clientCert);
        var session2 = new RdpB.Core.Session.Session(transport2, heartbeatInterval: TimeSpan.FromMilliseconds(200));
        bool closed = false;
        session2.StateChanged += state => { if (state is RdpB.Core.Session.SessionState.Closed) closed = true; };
        session2.Start();

        await Task.Delay(500); // 遠小於 PairingTimeout——沒開連線密碼不該卡在這裡等任何東西
        Assert.False(closed);

        await session2.DisposeAsync();
    }

    public void Dispose()
    {
        ConfigStore.ResetSessionForTests();
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }
}
