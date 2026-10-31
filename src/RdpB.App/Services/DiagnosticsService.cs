using System.Diagnostics;
using RdpB.Core.Transport;

namespace RdpB.App.Services;

/// <summary>計畫 §8 診斷頁要求的那段:「連線測試會依序試 QUIC 直連 → TCP 直連 → 代理,
/// 每一步顯示成功或失敗,以及原因」——跟 <see cref="ConnectivityChecker"/> 不一樣,那個是
/// 「測單一次最後會怎麼連」(走 TransportMode.Auto 讓它自己選、自己退),這裡是刻意**分開**
/// 三步各測一次,就算第一步成功也繼續測後面幾步,讓使用者看得出每一條路各自通不通,
/// 不是只看到最後選中的那一條。</summary>
public static class DiagnosticsService
{
    public sealed record StepResult(string StepName, bool Attempted, bool Success, string Message, TimeSpan Elapsed);

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);

    /// <summary><paramref name="proxy"/> 是 null 時「透過代理」那一步直接標成「沒有設定,
    /// 略過」,不嘗試連線(沒有代理可以測)。</summary>
    public static async Task<IReadOnlyList<StepResult>> RunAsync(
        string host, int quicPort, int tcpPort, ProxyOptions? proxy, CancellationToken ct = default)
    {
        // async lambda(不是直接傳方法群組)——QuicTransport.ConnectAsync/TcpTlsTransport.ConnectAsync
        // 回傳的是各自的具體型別(Task&lt;QuicTransport&gt;/Task&lt;TcpTlsTransport&gt;),C# 的
        // Task&lt;T&gt; 不是共變的,不能直接當 Func&lt;CancellationToken, Task&lt;ITransport&gt;&gt; 用,
        // 要靠 async lambda 的 await 把回傳值轉成 ITransport 再包回 Task。
        var results = new List<StepResult>
        {
            await ProbeAsync("QUIC 直連",
                async probeCt => await QuicTransport.ConnectAsync(host, quicPort, _ => true, probeCt).ConfigureAwait(false),
                ct).ConfigureAwait(false),
            await ProbeAsync("TCP+TLS 直連",
                async probeCt => await TcpTlsTransport.ConnectAsync(host, tcpPort, proxy: null, _ => true, probeCt).ConfigureAwait(false),
                ct).ConfigureAwait(false),
        };

        if (proxy is null)
        {
            results.Add(new StepResult("透過代理", Attempted: false, Success: false, "沒有設定代理,略過這一步", TimeSpan.Zero));
        }
        else
        {
            results.Add(await ProbeAsync("透過代理",
                async probeCt => await TcpTlsTransport.ConnectAsync(host, tcpPort, proxy, _ => true, probeCt).ConfigureAwait(false),
                ct).ConfigureAwait(false));
        }

        foreach (var r in results)
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[診斷] {r.StepName}:{(r.Attempted ? (r.Success ? "成功" : "失敗") : "略過")} — {r.Message}");

        return results;
    }

    private static async Task<StepResult> ProbeAsync(string stepName, Func<CancellationToken, Task<ITransport>> connect, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StepTimeout);

        try
        {
            var transport = await connect(cts.Token).ConfigureAwait(false);
            sw.Stop();
            await transport.CloseAsync().ConfigureAwait(false);
            return new StepResult(stepName, Attempted: true, Success: true, $"連線成功,耗時 {sw.ElapsedMilliseconds}ms", sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new StepResult(stepName, Attempted: true, Success: false, $"逾時({StepTimeout.TotalSeconds:0} 秒內沒連上)", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new StepResult(stepName, Attempted: true, Success: false, DescribeFailure(ex), sw.Elapsed);
        }
    }

    /// <summary>跟 ConnectivityChecker.DescribeFailure 同一套白話文轉換(計畫 §8:
    /// 「具體原因用白話文」)——這裡特意重複一份而不是抽共用方法,因為診斷頁要分別標出
    /// 「QUIC 被擋」「偵測到 TUN 代理」這類更細的描述(計畫 §8 原文舉的例子),之後兩邊的
    /// 措辭會分道揚鑣,現在先各自獨立,不要為了現在的重複硬共用一個以後會互相牽制的方法。</summary>
    private static string DescribeFailure(Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused
            => "對方沒有開啟 Host,或防火牆擋住了",
        System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound
            => "找不到這個位址,檢查一下 IP 或名稱有沒有打對",
        System.Net.Sockets.SocketException se => $"網路層錯誤:{se.Message}",
        System.Security.Authentication.AuthenticationException => "TLS 交握失敗,憑證可能有問題",
        System.Net.Quic.QuicException => "QUIC 連不上(可能被網路擋住,或對方沒開 QUIC 監聽)",
        _ => ex.Message,
    };
}
