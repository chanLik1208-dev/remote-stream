using System.Security.Cryptography.X509Certificates;

namespace RdpB.Core.Transport;

public enum TransportMode { Auto, Direct, Tcp, Ws }

public enum TransportKind { Quic, Tcp, Ws }

/// <summary>連線結果,帶著「最後實際用了哪一種」給 §8 診斷頁/HUD 用,不用另外猜。</summary>
public sealed record ConnectResult(ITransport Transport, TransportKind Kind, string? FallbackReason);

/// <summary>
/// 計畫 §1「自動選擇」:先試 QUIC,1.5 秒內連不上就退回 TCP。指定走代理時直接走 TCP/WS
/// (QUIC 是 UDP,一般 SOCKS5/HTTP CONNECT 代理不轉發 UDP,所以有代理就不試 QUIC)。
/// </summary>
public static class TransportConnector
{
    private static readonly TimeSpan QuicAttemptTimeout = TimeSpan.FromSeconds(1.5);

    public static async Task<ConnectResult> ConnectAsync(
        TransportMode mode,
        string host,
        int quicPort,
        int tcpPort,
        ProxyOptions? proxy,
        Func<X509Certificate2?, bool> validateServerCertificate,
        CancellationToken ct = default)
    {
        if (mode == TransportMode.Direct && proxy is not null)
            throw new InvalidOperationException("DIRECT 模式不應該帶代理設定(§6:DIRECT 一律不讀代理)");

        bool shouldTryQuic = proxy is null && mode is TransportMode.Auto or TransportMode.Direct;

        if (shouldTryQuic)
        {
            using var quicCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quicCts.CancelAfter(QuicAttemptTimeout);
            try
            {
                var quic = await QuicTransport.ConnectAsync(host, quicPort, validateServerCertificate, quicCts.Token)
                    .ConfigureAwait(false);
                return new ConnectResult(quic, TransportKind.Quic, null);
            }
            catch (Exception ex) when (mode == TransportMode.Auto)
            {
                // Auto 模式才自動退回;Direct 模式下 QUIC 失敗直接往下走 TCP+TLS(還是 DIRECT,只是換協定)。
                ct.ThrowIfCancellationRequested();
                return await ConnectTcpAsync(host, tcpPort, null, validateServerCertificate,
                    fallbackReason: DescribeQuicFailure(ex), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (mode == TransportMode.Direct)
            {
                ct.ThrowIfCancellationRequested();
                return await ConnectTcpAsync(host, tcpPort, null, validateServerCertificate,
                    fallbackReason: DescribeQuicFailure(ex), ct).ConfigureAwait(false);
            }
        }

        return mode switch
        {
            TransportMode.Ws => throw new NotSupportedException("WebSocket+TLS transport 還沒實作(排在之後的步驟)"),
            _ => await ConnectTcpAsync(host, tcpPort, proxy, validateServerCertificate, fallbackReason: null, ct).ConfigureAwait(false),
        };
    }

    private static async Task<ConnectResult> ConnectTcpAsync(
        string host, int port, ProxyOptions? proxy,
        Func<X509Certificate2?, bool> validateServerCertificate,
        string? fallbackReason, CancellationToken ct)
    {
        var tcp = await TcpTlsTransport.ConnectAsync(host, port, proxy, validateServerCertificate, ct).ConfigureAwait(false);
        return new ConnectResult(tcp, TransportKind.Tcp, fallbackReason);
    }

    private static string DescribeQuicFailure(Exception ex) => ex switch
    {
        OperationCanceledException => $"QUIC 在 {QuicAttemptTimeout.TotalSeconds:0.#} 秒內沒有回應",
        _ => $"QUIC 連線失敗:{ex.Message}",
    };
}
