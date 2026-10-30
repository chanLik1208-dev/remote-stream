using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RdpB.Core.Transport.Obfuscation;

namespace RdpB.Core.Transport;

/// <summary>Host 端的 TCP+TLS 監聽殼,跟 <see cref="QuicTransportListener"/> 對稱。
/// §7 的偽裝只在 DIRECT 時由呼叫端傳進 obfuscator,走代理的連線另外用不偽裝的監聽(或共用
/// 同一個監聽,由呼叫端決定要不要傳 obfuscator——偽裝是「連線層面」的事,不是埠本身的事)。</summary>
public sealed class TcpTlsTransportListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _serverCertificate;
    private readonly IObfuscator _obfuscator;
    private readonly bool _clientCertificateRequired;

    private TcpTlsTransportListener(TcpListener listener, X509Certificate2 cert, IObfuscator obfuscator, bool clientCertRequired)
    {
        _listener = listener;
        _serverCertificate = cert;
        _obfuscator = obfuscator;
        _clientCertificateRequired = clientCertRequired;
    }

    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static TcpTlsTransportListener Start(
        IPEndPoint endpoint, X509Certificate2 serverCertificate,
        IObfuscator? obfuscator = null, bool clientCertificateRequired = true)
    {
        var listener = new TcpListener(endpoint);
        listener.Start();
        return new TcpTlsTransportListener(listener, serverCertificate, obfuscator ?? NoObfuscator.Instance, clientCertificateRequired);
    }

    public async Task<TcpTlsTransport> AcceptAsync(CancellationToken ct = default)
    {
        var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        client.NoDelay = true;
        Stream raw = client.GetStream();

        await _obfuscator.ServerHandshakeAsync(raw, ct).ConfigureAwait(false);

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
