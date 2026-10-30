using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace RdpB.Core.Transport;

/// <summary>
/// Host 端監聽:計畫 §1「Host 是監聽方」。包一層薄殼在 QuicListener 上,
/// 每 accept 到一條連線就包成 <see cref="QuicTransport"/>。
/// </summary>
public sealed class QuicTransportListener : IAsyncDisposable
{
    private static readonly SslApplicationProtocol Alpn = new("rdpb/1");

    private readonly QuicListener _listener;

    private QuicTransportListener(QuicListener listener) => _listener = listener;

    public int BoundPort => ((IPEndPoint)_listener.LocalEndPoint).Port;

    public static async Task<QuicTransportListener> StartAsync(
        IPEndPoint endpoint,
        X509Certificate2 serverCertificate,
        CancellationToken ct = default)
    {
        var options = new QuicListenerOptions
        {
            ListenEndPoint = endpoint,
            ApplicationProtocols = [Alpn],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                // 同樣要留名額:client 會開一條雙向 stream(可靠 channel)和很多單向 stream(Audio/Video)。
                MaxInboundBidirectionalStreams = 4,
                MaxInboundUnidirectionalStreams = 256,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = [Alpn],
                    ServerCertificate = serverCertificate,
                    // Client 憑證驗證(mTLS)交給配對模組的指紋釘選邏輯,這裡一律接受,
                    // 之後由上層檢查 QuicConnection.RemoteCertificate 的指紋決定要不要繼續用這條連線。
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            }),
        };

        var listener = await QuicListener.ListenAsync(options, ct).ConfigureAwait(false);
        return new QuicTransportListener(listener);
    }

    /// <summary>等下一個 client 連上來,回傳可用的 <see cref="QuicTransport"/>。</summary>
    public async Task<QuicTransport> AcceptAsync(CancellationToken ct = default)
    {
        var connection = await _listener.AcceptConnectionAsync(ct).ConfigureAwait(false);
        return await QuicTransport.FromAcceptedConnectionAsync(connection, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await _listener.DisposeAsync().ConfigureAwait(false);
}
