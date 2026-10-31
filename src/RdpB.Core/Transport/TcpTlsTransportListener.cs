using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RdpB.Core.Transport.Obfuscation;

namespace RdpB.Core.Transport;

/// <summary>Host 端的 TCP+TLS 監聽殼,跟 <see cref="QuicTransportListener"/> 對稱。
///
/// **§7 偽裝改成自動偵測,不是監聽器層級的固定開關**(Kyra 要求的改動:偽裝的核心價值是
/// 握手外觀本身,不是硬要佔用 3389 這個埠號——原本的設計是「某個監聽器固定套用 obfuscator、
/// 另一個固定不套」,逼著偽裝連線只能走另外開的 3389,一旦 3389 被別的東西佔用(最常見:
/// Windows 自己的 Remote Desktop 服務)整個偽裝就連不上,這是真的在 Kyra 機器上炸出來的
/// 坑)。改成每個監聽器(不管是主埠還是進階選項的 3389)都一樣:先 peek 連線的第一個 byte,
/// 是 <see cref="RdpCamouflageObfuscator.TpktVersionByte"/>(0x03,TPKT 版本號)就先跑一次
/// 偽裝的 server handshake 再進 TLS,不是就直接進 TLS——TLS record 的第一個 byte(content
/// type)是 20~23 之間,跟 TPKT 的 3 不會混到,這個判斷是安全的,不會有「普通 TLS 連線被誤判
/// 成偽裝」這種情況。這樣同一個監聽器(埠)天生就能同時接受「有套偽裝的連線」和「沒套偽裝的
/// 直連」,使用者連去哪個埠完全是 Client 端自己決定,不用 Host 這邊另外配置。</summary>
public sealed class TcpTlsTransportListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _serverCertificate;
    private readonly bool _clientCertificateRequired;

    private TcpTlsTransportListener(TcpListener listener, X509Certificate2 cert, bool clientCertRequired)
    {
        _listener = listener;
        _serverCertificate = cert;
        _clientCertificateRequired = clientCertRequired;
    }

    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static TcpTlsTransportListener Start(
        IPEndPoint endpoint, X509Certificate2 serverCertificate, bool clientCertificateRequired = true)
    {
        var listener = new TcpListener(endpoint);
        listener.Start();
        return new TcpTlsTransportListener(listener, serverCertificate, clientCertificateRequired);
    }

    public async Task<TcpTlsTransport> AcceptAsync(CancellationToken ct = default)
    {
        var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        client.NoDelay = true;
        Stream raw = client.GetStream();

        var peekBuffer = new byte[1];
        int peeked = await client.Client.ReceiveAsync(peekBuffer, SocketFlags.Peek, ct).ConfigureAwait(false);
        if (peeked > 0 && peekBuffer[0] == RdpCamouflageObfuscator.TpktVersionByte)
            await new RdpCamouflageObfuscator().ServerHandshakeAsync(raw, ct).ConfigureAwait(false);

        var ssl = new SslStream(raw, leaveInnerStreamOpen: false,
            (_, _, _, _) => true); // Client 憑證驗證交給配對模組的指紋釘選邏輯(跟 QUIC 側一致)

        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _serverCertificate,
            ClientCertificateRequired = _clientCertificateRequired,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        }, ct).ConfigureAwait(false);

        return TcpTlsTransport.FromAcceptedStream(ssl, client);
    }

    public void Dispose() => _listener.Stop();
}
