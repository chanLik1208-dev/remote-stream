using RdpB.App.Services;
using RdpB.Core.Pairing;
using RdpB.Core.Transport;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>紅隊自我攻擊測試+手動安全審查都點名的缺口:指紋釘選機制對「私鑰外洩」沒有
/// 防禦力,唯一的事後補救是撤銷信任——這裡測撤銷真的有效(撤銷後要重新走 PIN 才能再信任),
/// 不是只改了畫面上的清單。不需要 GPU/擷取,所以不用標 Hardware。</summary>
public class TrustedClientRevocationTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"rdpb-revoke-test-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task AfterPairing_ClientAppearsInTrustedClientsList()
    {
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        var clientCert = EphemeralCertificate.CreateSelfSigned("revoke-test-client");
        await using var transport = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: clientCert);
        var session = new RdpB.Core.Session.Session(transport, heartbeatInterval: TimeSpan.FromSeconds(10));
        session.Start();

        var pairResult = await PairingCoordinator.PairAsync(session, host.Pin!, transport.RemoteCertificate!, clientCert);
        Assert.Equal(PairingResult.Success, pairResult);

        await Task.Delay(100); // RecordTrustedClient 在 Host 端處理完 PairAck 之後才寫,給一點時間

        var trusted = host.GetTrustedClients();
        var expectedFingerprint = Convert.ToHexString(PairingService.ComputeFingerprint(clientCert.RawData));
        Assert.Contains(trusted, c => string.Equals(c.FingerprintHex, expectedFingerprint, StringComparison.OrdinalIgnoreCase));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task RevokeTrustedClient_RemovesFromList_AndNextConnectionNeedsPinAgain()
    {
        await using var host = new HostListenerService(_configPath);
        host.Start(port: 0);

        var clientCert = EphemeralCertificate.CreateSelfSigned("revoke-test-client-2");
        var fingerprint = Convert.ToHexString(PairingService.ComputeFingerprint(clientCert.RawData));

        // 第一次配對,拿到信任。
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

        // 撤銷。
        host.RevokeTrustedClient(fingerprint);
        Assert.DoesNotContain(host.GetTrustedClients(), c => string.Equals(c.FingerprintHex, fingerprint, StringComparison.OrdinalIgnoreCase));

        // 撤銷之後,同一張憑證重新連線,應該被當成沒配對過——用錯的 PIN 連,應該失敗
        // (如果撤銷沒生效、還被當成已信任,Host 根本不會檢查 PIN,這裡會錯誤地配對成功)。
        await using var transport2 = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", host.Port, proxy: null, validateServerCertificate: _ => true, clientCertificate: clientCert);
        var session2 = new RdpB.Core.Session.Session(transport2, heartbeatInterval: TimeSpan.FromSeconds(10));
        session2.Start();
        var wrongPinResult = await PairingCoordinator.PairAsync(
            session2, "000000" == host.Pin ? "111111" : "000000", transport2.RemoteCertificate!, clientCert);
        Assert.NotEqual(PairingResult.Success, wrongPinResult);

        await session2.DisposeAsync();
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }
}
