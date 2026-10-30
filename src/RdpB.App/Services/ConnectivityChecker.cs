using System.Diagnostics;
using RdpB.Core.Transport;

namespace RdpB.App.Services;

public sealed record ConnectivityResult(bool Success, TransportKind? Kind, string Message, TimeSpan Elapsed);

/// <summary>
/// 計畫 §8 診斷頁的核心:「連線測試會依序試 QUIC 直連 → TCP 直連 → 代理」。
/// 直接用已經測過的 <see cref="TransportConnector"/>,連上就馬上關掉,只是要確認連得到、
/// 量一下要多久、走的是哪一種 transport。
///
/// **尚未接線的部分**:這裡的憑證驗證先一律接受(<c>_ =&gt; true</c>),純粹是連線層面的
/// 健檢,還沒套用配對/指紋釘選——配對流程(計畫 §2)還沒真正串起來,這個落差要等那塊做完
/// 才會補上,不然現在這個方法連不到任何東西(沒有人會通過嚴格驗證)。
/// </summary>
public static class ConnectivityChecker
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<ConnectivityResult> CheckAsync(
        string host, int quicPort, int tcpPort, ProxyOptions? proxy, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            var mode = proxy is null ? TransportMode.Auto : TransportMode.Tcp;
            var result = await TransportConnector.ConnectAsync(
                mode, host, quicPort, tcpPort, proxy,
                validateServerCertificate: _ => true, // 診斷用,見類別註解
                cts.Token).ConfigureAwait(false);

            sw.Stop();
            await result.Transport.CloseAsync().ConfigureAwait(false);

            string kindText = result.Kind switch
            {
                TransportKind.Quic => "QUIC 直連",
                TransportKind.Tcp => result.FallbackReason is null ? "TCP+TLS" : $"TCP+TLS(QUIC 失敗退回:{result.FallbackReason})",
                _ => result.Kind.ToString() ?? "未知",
            };
            return new ConnectivityResult(true, result.Kind, $"連線成功,{kindText},耗時 {sw.ElapsedMilliseconds}ms", sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ConnectivityResult(false, null, $"逾時({Timeout.TotalSeconds:0}秒內沒連上)", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectivityResult(false, null, $"連線失敗:{DescribeFailure(ex)}", sw.Elapsed);
        }
    }

    /// <summary>把常見的連線例外換成 Kyra 看得懂的白話文(計畫 §8:「具體原因用白話文」)。</summary>
    private static string DescribeFailure(Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused
            => "對方沒有開啟 Host,或防火牆擋住了",
        System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound
            => "找不到這個位址,檢查一下 IP 或名稱有沒有打對",
        System.Net.Sockets.SocketException se => $"網路層錯誤:{se.Message}",
        System.Security.Authentication.AuthenticationException => "TLS 交握失敗,憑證可能有問題",
        _ => ex.Message,
    };
}
