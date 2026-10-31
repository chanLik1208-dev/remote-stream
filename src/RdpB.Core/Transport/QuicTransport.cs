using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using RdpB.Core.Protocol;

namespace RdpB.Core.Transport;

/// <summary>
/// §1 的 QUIC transport(<c>System.Net.Quic</c>,Win11 內建 msquic)。
///
/// 一條連線裡:
///  - **一條可靠雙向 stream**(由 client 主動開)扛 Control/Input——跟 TCP transport
///    共用同一套 <see cref="FrameStreamPump"/>,因為 QuicStream 本身就是 .NET 的 Stream。
///  - **Audio/Video/其他可丟棄訊息,每筆開一條單向 stream**,過期(佇列深度超過門檻時最舊的)
///    直接 <c>Abort</c> 掉,不會塞住後面的 stream——這正是 QUIC 相對 TCP 的優勢:一條 stream
///    卡住不會拖累其他 stream(TCP 單一連線上就需要 §1 的 ChannelSendQueue 手動模擬這件事)。
/// </summary>
public sealed class QuicTransport : ITransport
{
    private static readonly SslApplicationProtocol Alpn = new("rdpb/1");

    private readonly QuicConnection _connection;
    private readonly ChannelSendQueue _reliableQueue = new();
    private readonly FrameStreamPump _reliablePump;
    private readonly ConcurrentDictionary<Channel, ConcurrentQueue<QuicStream>> _inFlightDroppable = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _closed;

    public bool IsConnected => !_closed;

    /// <summary>對面在 QUIC/TLS handshake 時實際出示的憑證(mTLS)——跟
    /// <see cref="TcpTlsTransport.RemoteCertificate"/> 同一個用途,配對驗證(計畫 §2)
    /// 要用這個,不能信 Pair 訊息裡自稱的指紋。</summary>
    public X509Certificate2? RemoteCertificate { get; }

    public event MessageReceivedHandler? MessageReceived;
    public event Action<Exception?>? Disconnected;

    /// <summary>Video channel 的某個單向 stream 因為太舊被 abort 時觸發。</summary>
    public event Action<Channel, int>? FrameDropped;

    private QuicTransport(QuicConnection connection, QuicStream reliableStream)
    {
        _connection = connection;
        RemoteCertificate = connection.RemoteCertificate as X509Certificate2
            ?? (connection.RemoteCertificate is null ? null : new X509Certificate2(connection.RemoteCertificate));
        _reliablePump = new FrameStreamPump(reliableStream, _reliableQueue);
        _reliablePump.MessageReceived += (ch, type, payload) => MessageReceived?.Invoke(ch, type, payload);
        _reliablePump.Faulted += ex => _ = FailAsync(ex);
    }

    /// <summary>Client 端:連到 host:port,開出可靠 stream,開始接收迴圈。只走 DIRECT,不支援代理
    /// (QUIC 是 UDP,代理節點大多不穩定轉發——計畫 §1「自動選擇」會在這裡失敗時退回 TCP)。</summary>
    public static async Task<QuicTransport> ConnectAsync(
        string host,
        int port,
        Func<X509Certificate2?, bool> validateServerCertificate,
        CancellationToken ct = default,
        X509Certificate2? clientCertificate = null)
    {
        var clientAuthOptions = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ApplicationProtocols = [Alpn],
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
                validateServerCertificate(cert is null ? null : new X509Certificate2(cert)),
        };
        // mTLS,跟 TcpTlsTransport.ConnectAsync 同一個用途(計畫 §2 的配對需要 Host
        // 看到 Client 實際出示的憑證)——沒傳就是單向 TLS,舊行為不變。
        if (clientCertificate is not null)
            clientAuthOptions.ClientCertificates = [clientCertificate];

        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(await ResolveToIpAsync(host, ct).ConfigureAwait(false), port),
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            // 預設是 0(不接受任何對方開的 stream)。對方可能用一條額外的雙向 stream(目前沒用到,
            // 保留餘裕)和很多單向 stream(Audio/Video,§1)回送給我們,兩種都要留夠名額。
            MaxInboundBidirectionalStreams = 4,
            MaxInboundUnidirectionalStreams = 256,
            ClientAuthenticationOptions = clientAuthOptions,
        };

        var connection = await QuicConnection.ConnectAsync(clientOptions, ct).ConfigureAwait(false);
        var reliableStream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);

        // QUIC 的 stream 要等真的寫過資料才會在對面出現(光是 Open 只是本地配置一個 stream id,
        // 不會送出任何 STREAM frame)。如果沒有這一步,server 端的 AcceptInboundStreamAsync 會一路
        // 等到 idle timeout——必須馬上送一個 priming frame,把這條 stream「打」到對面去。
        var hello = new Frame(Channel.Control, MessageType.Hello, ReadOnlyMemory<byte>.Empty);
        await reliableStream.WriteAsync(hello.ToBytes(), ct).ConfigureAwait(false);

        var transport = new QuicTransport(connection, reliableStream);
        transport._reliablePump.Start(transport._cts.Token);
        _ = Task.Run(() => transport.AcceptLoopAsync(transport._cts.Token));
        return transport;
    }

    /// <summary>Host 端:包裝一個已經 accept 到的連線。等 client 開出可靠 stream 後才算就緒。</summary>
    public static async Task<QuicTransport> FromAcceptedConnectionAsync(QuicConnection connection, CancellationToken ct = default)
    {
        var first = await connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
        if (first.Type != QuicStreamType.Bidirectional)
            throw new InvalidOperationException("QUIC:預期第一條進來的 stream 是可靠雙向 stream,收到的卻不是");

        // 消耗掉 ConnectAsync 送的 priming Hello frame(見上方說明)。這是 transport 內部的握手
        // 動作,不該冒出去變成應用層收到的一筆訊息。
        await ConsumePrimingHelloAsync(first, ct).ConfigureAwait(false);

        var transport = new QuicTransport(connection, first);
        transport._reliablePump.Start(transport._cts.Token);
        _ = Task.Run(() => transport.AcceptLoopAsync(transport._cts.Token));
        return transport;
    }

    public Task SendAsync(Channel channel, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_closed) throw new ObjectDisposedException(nameof(QuicTransport));

        if (!ChannelPolicy.IsDroppable(channel))
        {
            _reliableQueue.Enqueue(new Frame(channel, type, payload));
            return Task.CompletedTask;
        }

        return SendOnFreshUnidirectionalStreamAsync(channel, type, payload, ct);
    }

    private async Task SendOnFreshUnidirectionalStreamAsync(Channel channel, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct).ConfigureAwait(false);

        var q = _inFlightDroppable.GetOrAdd(channel, _ => new ConcurrentQueue<QuicStream>());
        q.Enqueue(stream);
        int limit = ChannelPolicy.QueueDepth(channel);
        while (q.Count > limit && q.TryDequeue(out var stale))
        {
            try { stale.Abort(QuicAbortDirection.Write, 0); } catch { /* 可能已經寫完了 */ }
            FrameDropped?.Invoke(channel, q.Count + 1);
        }

        try
        {
            var headerBuf = new byte[Frame.HeaderSize];
            new Frame(channel, type, payload).WriteHeaderTo(headerBuf);
            await stream.WriteAsync(headerBuf, ct).ConfigureAwait(false);
            if (payload.Length > 0)
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.WriteAsync(ReadOnlyMemory<byte>.Empty, completeWrites: true, ct).ConfigureAwait(false);
        }
        catch (QuicException)
        {
            // 這條 stream 被 abort 或連線出問題——對可丟棄訊息來說不是致命錯誤,忽略即可。
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = await _connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
                if (stream.Type == QuicStreamType.Unidirectional)
                {
                    _ = Task.Run(() => ReadSingleFrameStreamAsync(stream, ct), ct);
                }
                else
                {
                    // 不支援第二條可靠雙向 stream,直接關掉。
                    try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { /* 正常關閉 */ }
        catch (Exception ex) { _ = FailAsync(ex); }
    }

    private async Task ReadSingleFrameStreamAsync(QuicStream stream, CancellationToken ct)
    {
        try
        {
            var headerBuf = new byte[Frame.HeaderSize];
            await ReadExactAsync(stream, headerBuf, ct).ConfigureAwait(false);

            var channel = (Channel)headerBuf[0];
            var type = (MessageType)headerBuf[1];
            int len = BitConverter.ToInt32(headerBuf, 2);
            if (len < 0 || len > 64 * 1024 * 1024)
                throw new InvalidDataException($"收到不合理的 frame 長度:{len}");

            var payload = len == 0 ? Array.Empty<byte>() : new byte[len];
            if (len > 0)
                await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);

            MessageReceived?.Invoke(channel, type, payload);
        }
        catch
        {
            // 單一可丟棄訊息的 stream 讀失敗(例如被對面 abort),不影響整條連線。
        }
        finally
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static async Task ConsumePrimingHelloAsync(QuicStream reliableStream, CancellationToken ct)
    {
        var headerBuf = new byte[Frame.HeaderSize];
        await ReadExactAsync(reliableStream, headerBuf, ct).ConfigureAwait(false);
        int len = BitConverter.ToInt32(headerBuf, 2);
        if (len > 0)
            await ReadExactAsync(reliableStream, new byte[len], ct).ConfigureAwait(false);
        // 不檢查內容是不是真的 Hello——對面若不照這個協定走,之後的 frame 解析自然會出錨,
        // 這裡只負責把第一筆丟掉。
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset, buf.Length - offset), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("stream 被對面關閉,資料不完整");
            offset += n;
        }
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
        await _reliablePump.StopAsync().ConfigureAwait(false);
        try { await _connection.CloseAsync(0).ConfigureAwait(false); } catch { }
        try { await _connection.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    /// <summary>QuicClientConnectionOptions.RemoteEndPoint 要一個 IPEndPoint,不像 TcpClient.ConnectAsync
    /// 那樣接受主機名字串自己做 DNS——以前這裡直接 <c>IPAddress.Parse(host)</c>,host 只要不是純 IP
    /// (配對紀錄存的通常是裝置名稱/網域,例如 DDNS 的 <c>xxx.netip.genius427.site</c>)就會直接丟
    /// <c>FormatException("An invalid IP address was specified.")</c>——這是真的在 Kyra 機器上炸出來的
    /// 坑,表現成「QUIC 連線失敗」然後自動退回 TCP+TLS,HUD 上完全看不出真正原因是「根本沒做過 DNS
    /// 解析」。現在先判斷是不是已經是合法 IP(直連區網常見),不是的話才真的呼叫 <see cref="Dns"/> 查詢,
    /// 優先選 IPv4(QUIC/UDP 走 IPv6 在很多家用路由器/ISP 上還是不穩),查不到就讓例外自然往外飄,
    /// 外層的 Auto 模式會照常把這個失敗原因顯示出來、退回 TCP+TLS。</summary>
    private static async Task<IPAddress> ResolveToIpAsync(string host, CancellationToken ct)
    {
        if (host == "localhost") return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out var literal)) return literal;

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        foreach (var addr in addresses)
            if (addr.AddressFamily == AddressFamily.InterNetwork)
                return addr;

        return addresses[0];
    }
}
