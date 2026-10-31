using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RdpB.Core.Transport;

/// <summary>Host 端的 WebSocket+TLS 監聽殼,跟 <see cref="TcpTlsTransportListener"/> 對稱——
/// 差別只在 TLS handshake 完成之後多跑一次 <see cref="WsTransport.ServerUpgradeHandshakeAsync"/>
/// (HTTP Upgrade)。不用 HttpListener/Kestrel,見 <see cref="WsTransport"/> 類別上的註解。</summary>
public sealed class WsTransportListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _serverCertificate;
    private readonly bool _clientCertificateRequired;

    private WsTransportListener(TcpListener listener, X509Certificate2 cert, bool clientCertRequired)
    {
        _listener = listener;
        _serverCertificate = cert;
        _clientCertificateRequired = clientCertRequired;
    }

    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static WsTransportListener Start(
        IPEndPoint endpoint, X509Certificate2 serverCertificate, bool clientCertificateRequired = true)
    {
        var listener = new TcpListener(endpoint);
        listener.Start();
        return new WsTransportListener(listener, serverCertificate, clientCertificateRequired);
    }

    public async Task<WsTransport> AcceptAsync(CancellationToken ct = default)
    {
        var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        client.NoDelay = true;
        Stream raw = client.GetStream();

        var ssl = new SslStream(raw, leaveInnerStreamOpen: false,
            (_, _, _, _) => true); // Client 憑證驗證交給配對模組的指紋釘選邏輯(跟 TCP/QUIC 側一致)

        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _serverCertificate,
            ClientCertificateRequired = _clientCertificateRequired,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        }, ct).ConfigureAwait(false);

        await WsTransport.ServerUpgradeHandshakeAsync(ssl, ct).ConfigureAwait(false);

        var remoteCert = ssl.RemoteCertificate as X509Certificate2 ??
            (ssl.RemoteCertificate is null ? null : new X509Certificate2(ssl.RemoteCertificate));

        var socket = WebSocket.CreateFromStream(ssl, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
        return WsTransport.FromAcceptedSocket(socket, ssl, client, remoteCert);
    }

    public void Dispose() => _listener.Stop();
}
