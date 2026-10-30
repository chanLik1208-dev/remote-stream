using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RdpB.Core.Protocol;
using RdpB.Core.Transport.Obfuscation;
using RdpB.Core.Transport.Proxy;

namespace RdpB.Core.Transport;

/// <summary>
/// 連線用的代理設定。空的話就是 DIRECT。
/// </summary>
public sealed record ProxyOptions(ProxyKind Kind, string Host, int Port, Socks5Credentials? Socks5Credentials = null, string? HttpProxyAuthorizationHeader = null);

public enum ProxyKind { Socks5, HttpConnect }

/// <summary>
/// §1 的 TCP+TLS transport:單一連線,§Frame 的自訂 frame header,`NoDelay = true`。
/// 實際的讀寫迴圈交給 <see cref="FrameStreamPump"/>——QUIC transport 的可靠 bidi stream
/// 用的是同一套邏輯,不要兩邊各寫一份。
/// </summary>
public sealed class TcpTlsTransport : ITransport
{
    private readonly Stream _stream;
    private readonly TcpClient? _ownedClient; // 由這個 transport 負責 Dispose 的底層 socket(client 端建立時才有)
    private readonly ChannelSendQueue _sendQueue = new();
    private readonly FrameStreamPump _pump;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _closed;

    public bool IsConnected => !_closed;

    /// <summary>對面在 TLS handshake 時實際出示的憑證(mTLS)。計畫 §2 的配對驗證要用這個,
    /// 不能只信任 Pair 訊息裡「自稱」的指紋——一定要跟這個欄位算出來的指紋比對,
    /// 不然誰都可以在 Pair 訊息裡謊報一個指紋混過去。</summary>
    public X509Certificate2? RemoteCertificate { get; private set; }

    public event MessageReceivedHandler? MessageReceived;
    public event Action<Exception?>? Disconnected;

    /// <summary>Video channel 因為佇列塞滿丟幀時觸發,呼叫端(Host 的編碼器)應該據此立刻出一張 IDR。</summary>
    public event Action<Channel, int>? FrameDropped
    {
        add => _sendQueue.FrameDropped += value;
        remove => _sendQueue.FrameDropped -= value;
    }

    private TcpTlsTransport(Stream authenticatedStream, TcpClient? ownedClient)
    {
        _stream = authenticatedStream;
        _ownedClient = ownedClient;
        _pump = new FrameStreamPump(_stream, _sendQueue);
        _pump.MessageReceived += (ch, type, payload) => MessageReceived?.Invoke(ch, type, payload);
        _pump.Faulted += ex => _ = FailAsync(ex);
    }

    /// <summary>
    /// Client 端:連到 <paramref name="host"/>:<paramref name="port"/>(直連或透過 <paramref name="proxy"/>),
    /// 完成 TLS handshake,回傳可用的 transport。<paramref name="validateServerCertificate"/> 留給配對/
    /// 憑證釘選模組(計畫 §2)決定要不要接受對方的憑證。
    /// </summary>
    public static async Task<TcpTlsTransport> ConnectAsync(
        string host,
        int port,
        ProxyOptions? proxy,
        Func<X509Certificate2?, bool> validateServerCertificate,
        CancellationToken ct = default,
        IObfuscator? obfuscator = null,
        X509Certificate2? clientCertificate = null)
    {
        var client = new TcpClient { NoDelay = true };
        string connectHost = proxy?.Host ?? host;
        int connectPort = proxy?.Port ?? port;
        await client.ConnectAsync(connectHost, connectPort, ct).ConfigureAwait(false);

        Stream raw = client.GetStream();

        if (proxy is not null)
        {
            switch (proxy.Kind)
            {
                case ProxyKind.Socks5:
                    await Socks5Client.ConnectAsync(raw, host, port, proxy.Socks5Credentials, ct).ConfigureAwait(false);
                    break;
                case ProxyKind.HttpConnect:
                    await HttpConnectClient.ConnectAsync(raw, host, port, proxy.HttpProxyAuthorizationHeader, ct).ConfigureAwait(false);
                    break;
            }
        }

        // §6/§7:偽裝只在 DIRECT(沒有代理)時由呼叫端傳進來,走代理一律傳 null/NoObfuscator。
        if (proxy is null && obfuscator is not null)
            await obfuscator.ClientHandshakeAsync(raw, ct).ConfigureAwait(false);

        var ssl = new SslStream(raw, leaveInnerStreamOpen: false,
            (_, cert, _, _) => validateServerCertificate(cert is null ? null : new X509Certificate2(cert)));

        var clientAuthOptions = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        };
        // mTLS:Client 把自己的身分憑證秀出來,Host 才有東西可以在配對流程裡驗證指紋
        // (計畫 §2)。沒傳就是舊行為,單向 TLS。
        if (clientCertificate is not null)
            clientAuthOptions.ClientCertificates = [clientCertificate];

        await ssl.AuthenticateAsClientAsync(clientAuthOptions, ct).ConfigureAwait(false);

        var transport = new TcpTlsTransport(ssl, client) { RemoteCertificate = ssl.RemoteCertificate as X509Certificate2 ?? ToCert2(ssl.RemoteCertificate) };
        transport._pump.Start(transport._cts.Token);
        return transport;
    }

    /// <summary>Host 端:包裝一個已經完成 server 端 TLS handshake 的 SslStream(由 Host 的 listener 迴圈準備好)。</summary>
    public static TcpTlsTransport FromAcceptedStream(SslStream authenticatedStream, TcpClient? ownedClient = null)
    {
        var transport = new TcpTlsTransport(authenticatedStream, ownedClient)
        {
            RemoteCertificate = authenticatedStream.RemoteCertificate as X509Certificate2 ?? ToCert2(authenticatedStream.RemoteCertificate),
        };
        transport._pump.Start(transport._cts.Token);
        return transport;
    }

    private static X509Certificate2? ToCert2(System.Security.Cryptography.X509Certificates.X509Certificate? cert) =>
        cert is null ? null : new X509Certificate2(cert);

    public Task SendAsync(Channel channel, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_closed) throw new ObjectDisposedException(nameof(TcpTlsTransport));
        _sendQueue.Enqueue(new Frame(channel, type, payload));
        return Task.CompletedTask;
    }

    private Task FailAsync(Exception ex)
    {
        if (_closed) return Task.CompletedTask;
        _closed = true;
        Disconnected?.Invoke(ex);
        return Task.CompletedTask;
    }

    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        try { _stream.Dispose(); } catch { /* 已經在關閉中 */ }
        _ownedClient?.Dispose();
        await _pump.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
