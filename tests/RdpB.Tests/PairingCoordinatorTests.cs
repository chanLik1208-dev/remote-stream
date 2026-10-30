using System.Net;
using RdpB.Core.Pairing;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Tests.TestSupport;
using CoreSession = RdpB.Core.Session.Session;

namespace RdpB.Tests;

/// <summary>
/// 真的用兩個 TcpTlsTransport(loopback + 真憑證)跑一次配對流程,不是 mock——
/// 配對驗證的安全性剛好就建立在「TLS 實際出示的憑證」這件事上,用假的 transport 測不出意義。
/// </summary>
public class PairingCoordinatorTests
{
    private const string Pin = "482913";

    private static async Task<(CoreSession client, CoreSession host, TcpTlsTransportListener listener)> ConnectAsync(
        System.Security.Cryptography.X509Certificates.X509Certificate2 hostCert,
        System.Security.Cryptography.X509Certificates.X509Certificate2 clientCert)
    {
        var listener = TcpTlsTransportListener.Start(new IPEndPoint(IPAddress.Loopback, 0), hostCert, clientCertificateRequired: true);
        var acceptTask = listener.AcceptAsync();

        var clientTransport = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", listener.BoundPort, proxy: null, validateServerCertificate: _ => true,
            clientCertificate: clientCert);
        var hostTransport = await acceptTask;

        var clientSession = new CoreSession(clientTransport, heartbeatInterval: TimeSpan.FromMilliseconds(100));
        var hostSession = new CoreSession(hostTransport, heartbeatInterval: TimeSpan.FromMilliseconds(100));
        clientSession.Start();
        hostSession.Start();

        return (clientSession, hostSession, listener);
    }

    [Fact]
    public async Task CorrectPin_BothSidesSucceed_HostLearnsClientFingerprint()
    {
        var hostCert = EphemeralCertificate.CreateSelfSigned("host");
        var clientCert = EphemeralCertificate.CreateSelfSigned("client");
        var (clientSession, hostSession, listener) = await ConnectAsync(hostCert, clientCert);

        try
        {
            var hostTask = PairingCoordinator.WaitForPairingAsync(hostSession, Pin, clientCert, hostCert);
            var clientTask = PairingCoordinator.PairAsync(clientSession, Pin, hostCert, clientCert);

            var clientResult = await clientTask;
            var (hostResult, learnedFingerprint) = await hostTask;

            Assert.Equal(PairingResult.Success, clientResult);
            Assert.Equal(PairingResult.Success, hostResult);
            Assert.Equal(PairingService.ComputeFingerprint(clientCert.RawData), learnedFingerprint);
        }
        finally
        {
            await clientSession.DisposeAsync();
            await hostSession.DisposeAsync();
            listener.Dispose();
        }
    }

    [Fact]
    public async Task WrongPin_BothSidesFail()
    {
        var hostCert = EphemeralCertificate.CreateSelfSigned("host");
        var clientCert = EphemeralCertificate.CreateSelfSigned("client");
        var (clientSession, hostSession, listener) = await ConnectAsync(hostCert, clientCert);

        try
        {
            var hostTask = PairingCoordinator.WaitForPairingAsync(hostSession, "000000", clientCert, hostCert);
            var clientTask = PairingCoordinator.PairAsync(clientSession, "111111", hostCert, clientCert);

            var clientResult = await clientTask;
            var (hostResult, learnedFingerprint) = await hostTask;

            Assert.Equal(PairingResult.CertificateMismatch, clientResult); // Host 回了 failure ack
            Assert.Equal(PairingResult.WrongPin, hostResult);
            Assert.Null(learnedFingerprint);
        }
        finally
        {
            await clientSession.DisposeAsync();
            await hostSession.DisposeAsync();
            listener.Dispose();
        }
    }

    [Fact]
    public async Task SpoofedFingerprintClaim_IsRejected_EvenWithCorrectPinMath()
    {
        // 安全性測試:攻擊者的連線用 attackerCert 做 TLS(所以 Host 看到的 RemoteCertificate
        // 是 attackerCert),但 Pair 訊息裡謊稱自己是 victimCert、而且 HMAC tag 也是用
        // victimCert 的指紋算出來的(等於攻擊者偷看過 victim 的指紋,PIN 也知道)——
        // 這種情況 Host 必須拒絕,因為 TLS 實際憑證跟訊息自稱的指紋對不上。
        var hostCert = EphemeralCertificate.CreateSelfSigned("host");
        var attackerCert = EphemeralCertificate.CreateSelfSigned("attacker");
        var victimCert = EphemeralCertificate.CreateSelfSigned("victim");

        var (clientSession, hostSession, listener) = await ConnectAsync(hostCert, attackerCert);

        try
        {
            var hostTask = PairingCoordinator.WaitForPairingAsync(hostSession, Pin, attackerCert, hostCert);

            // 不呼叫 PairAsync(它只會老實用自己真正的憑證)——手動組一個謊報指紋的 Pair 訊息。
            var hostFingerprint = PairingService.ComputeFingerprint(hostCert.RawData);
            var victimFingerprint = PairingService.ComputeFingerprint(victimCert.RawData);
            var forgedTag = PairingService.ComputePairingTag(Pin, hostFingerprint, victimFingerprint);

            var payload = new byte[2 + victimFingerprint.Length + forgedTag.Length];
            payload[0] = (byte)victimFingerprint.Length;
            victimFingerprint.CopyTo(payload, 1);
            payload[1 + victimFingerprint.Length] = (byte)forgedTag.Length;
            forgedTag.CopyTo(payload, 2 + victimFingerprint.Length);

            await clientSession.SendAsync(Channel.Control, MessageType.Pair, payload);

            var result = await hostTask;
            Assert.Equal(PairingResult.CertificateMismatch, result.Result);
            Assert.Null(result.ClientFingerprint);
        }
        finally
        {
            await clientSession.DisposeAsync();
            await hostSession.DisposeAsync();
            listener.Dispose();
        }
    }
}
