using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RdpB.Core.Protocol;
using RdpB.Core.Transport.Proxy;

namespace RdpB.Core.Transport;

/// <summary>
/// 計畫 §1/§6 的 WebSocket+TLS transport——之前完全沒實作(<see cref="TransportConnector"/>
/// 丟 <c>NotSupportedException</c>)。用途跟 TCP+TLS 不一樣:不是給一般直連用的,是給
/// 「要躲在 CDN/反向代理後面」或「走 Xray fallback」這種情境用(計畫 §1 原文),所以這裡**不**
/// 套 §7 的 RDP 外觀偽裝(那是給裸連的 TCP+TLS 用的,WS 自己的「偽裝」就是看起來像一般
/// WebSocket 流量這件事本身)。
///
/// 不用 ASP.NET Core/HttpListener 起 WS server——那需要 http.sys URL ACL 或系統管理員權限,
/// 一般使用者的桌面 app 不該要求這個。改成自己手刻最小可用的 HTTP Upgrade 交握(GET + 101
/// Switching Protocols,RFC 6455 §4),交握完直接用 <see cref="WebSocket.CreateFromStream"/>
/// 把底下那條已經做完 mTLS 的 <see cref="SslStream"/> 包成標準的 <see cref="WebSocket"/>,
/// 之後的訊息framing/ping-pong 全部交給 BCL 自己處理,不用自己刻 RFC 6455 的 frame 格式。
///
/// 一個 WS 連線上的多工送出策略跟 TCP+TLS 完全一樣(單一連線,靠 <see cref="ChannelSendQueue"/>
/// 做優先序+丟幀),一筆 <see cref="Frame"/> 對應一個完整的 WS binary message,不用另外疊一層
/// 自訂 header——WS 訊息本身就有邊界,收端一個 message 收完就是一個完整 frame(見
/// <see cref="Frame.TryParse"/> 原本就是給 TCP/WS 共用設計的,見該類別上的註解)。
/// </summary>
public sealed class WsTransport : ITransport
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly WebSocket _socket;
    private readonly Stream _underlyingStream;
    private readonly TcpClient? _ownedClient;
    private readonly ChannelSendQueue _sendQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _closed;
    private Task? _sendLoop;
    private Task? _receiveLoop;

    public bool IsConnected => !_closed;

    /// <summary>對面在 TLS handshake 時實際出示的憑證(mTLS)——跟
    /// <see cref="TcpTlsTransport.RemoteCertificate"/> 同一個用途。</summary>
    public X509Certificate2? RemoteCertificate { get; }

    public event MessageReceivedHandler? MessageReceived;
    public event Action<Exception?>? Disconnected;

    /// <summary>跟 TCP transport 一樣,Video channel 因為佇列塞滿丟幀時觸發。</summary>
    public event Action<Channel, int>? FrameDropped
    {
        add => _sendQueue.FrameDropped += value;
        remove => _sendQueue.FrameDropped -= value;
    }

    private WsTransport(WebSocket socket, Stream underlyingStream, TcpClient? ownedClient, X509Certificate2? remoteCertificate)
    {
        _socket = socket;
        _underlyingStream = underlyingStream;
        _ownedClient = ownedClient;
        RemoteCertificate = remoteCertificate;
    }

    /// <summary>Client 端:連到 <paramref name="host"/>:<paramref name="port"/>(直連或透過
    /// <paramref name="proxy"/>),完成 mTLS,再跑一次 HTTP Upgrade 交握。</summary>
    public static async Task<WsTransport> ConnectAsync(
        string host,
        int port,
        ProxyOptions? proxy,
        Func<X509Certificate2?, bool> validateServerCertificate,
        CancellationToken ct = default,
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

        var ssl = new SslStream(raw, leaveInnerStreamOpen: false,
            (_, cert, _, _) => validateServerCertificate(cert is null ? null : new X509Certificate2(cert)));

        var clientAuthOptions = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        };
        if (clientCertificate is not null)
            clientAuthOptions.ClientCertificates = [clientCertificate];

        await ssl.AuthenticateAsClientAsync(clientAuthOptions, ct).ConfigureAwait(false);

        var remoteCert = ssl.RemoteCertificate as X509Certificate2 ??
            (ssl.RemoteCertificate is null ? null : new X509Certificate2(ssl.RemoteCertificate));

        await ClientUpgradeHandshakeAsync(ssl, host, ct).ConfigureAwait(false);

        var socket = WebSocket.CreateFromStream(ssl, isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
        var transport = new WsTransport(socket, ssl, client, remoteCert);
        transport.Start();
        return transport;
    }

    /// <summary>Host 端:包裝一個已經完成 server 端 TLS handshake、HTTP Upgrade 也做完的
    /// <see cref="WebSocket"/>(由 <see cref="WsTransportListener"/> 準備好)。</summary>
    internal static WsTransport FromAcceptedSocket(WebSocket socket, Stream underlyingStream, TcpClient ownedClient, X509Certificate2? remoteCertificate)
    {
        var transport = new WsTransport(socket, underlyingStream, ownedClient, remoteCertificate);
        transport.Start();
        return transport;
    }

    private void Start()
    {
        _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token));
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public Task SendAsync(Channel channel, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_closed) throw new ObjectDisposedException(nameof(WsTransport));
        _sendQueue.Enqueue(new Frame(channel, type, payload));
        return Task.CompletedTask;
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _sendQueue.DequeueAsync(ct).ConfigureAwait(false);
                await _socket.SendAsync(frame.ToBytes(), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常關閉 */ }
        catch (Exception ex) { _ = FailAsync(ex); }
    }

    // code review 抓到的真坑:這裡原本沒有任何上限檢查,對面(不管是惡意還是單純壞掉)
    // 送一個一直不標 EndOfMessage 的訊息,ms 會無限長大,吃光記憶體——TCP(FrameStreamPump)
    // 和 QUIC 都有這個上限(見兩邊的 64*1024*1024 檢查),WS 這條路漏掉了,這裡補齊同一個
    // 數字,行為也跟它們一致:超過就當協定違規直接丟例外,讓這條連線整個斷掉。
    private const int MaxMessageSize = 64 * 1024 * 1024;

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var chunk = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(chunk, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _ = FailAsync(null);
                        return;
                    }
                    if (ms.Length + result.Count > MaxMessageSize)
                        throw new InvalidDataException($"收到的 WebSocket message 超過上限({MaxMessageSize} bytes),對面可能沒有照協定走");
                    ms.Write(chunk, 0, result.Count);
                } while (!result.EndOfMessage);

                // 一個 WS message 就該剛好是一個完整 Frame(見類別上的註解)——code review
                // 抓到的真坑:原本只看 TryParse 的 bool,沒檢查 consumed 是不是等於整個訊息
                // 長度。只要訊息前 6 bytes 剛好湊出一個「看起來合法」的長度(例如被截斷、
                // 或攻擊者故意塞的長度不等於實際訊息長度),TryParse 一樣會回 true,consumed
                // 卻小於 ms.Length——照舊往下送出去,等於把一包內容被截斷/偽造長度的訊息當
                // 正常訊息處理,跟上面那行註解「對面沒有照協定走就直接忽略」講的不是同一件事。
                // 這裡把 consumed 也一起驗,對不上就真的忽略,不冒充完整訊息往上送。
                if (Frame.TryParse(ms.GetBuffer().AsSpan(0, (int)ms.Length), out var frame, out int consumed)
                    && consumed == ms.Length)
                {
                    MessageReceived?.Invoke(frame.Channel, frame.Type, frame.Payload);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常關閉 */ }
        catch (Exception ex) { _ = FailAsync(ex); }
    }

    private Task FailAsync(Exception? ex)
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

        // **不**用 CloseAsync(完整的 RFC 6455 close 交握,送 Close frame 還要等對面回
        // Close frame)——我們自己的協定(Session 的 Bye/心跳)已經負責優雅斷線的語意,
        // 傳輸層這裡只要「馬上停下來」。兩邊幾乎同時呼叫這裡的話,各自等對面的 Close frame
        // 回應會互相卡住(真的在這台機器上炸過:測試裡 client/server 幾乎同時 Dispose,
        // CloseAsync 兩邊都卡死等不到回應)。Abort() 是同步、立即的,不等任何回應。
        try { _socket.Abort(); } catch { }
        try { _underlyingStream.Dispose(); } catch { }
        _ownedClient?.Dispose();

        if (_sendLoop is not null) { try { await _sendLoop.ConfigureAwait(false); } catch { } }
        if (_receiveLoop is not null) { try { await _receiveLoop.ConfigureAwait(false); } catch { } }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    /// <summary>Client 端 HTTP Upgrade 交握(RFC 6455 §4.1)。只送最少需要的 header,
    /// 不處理 Sec-WebSocket-Protocol 等協商,這條連線的用途(傳我們自己的 Frame 格式)
    /// 兩邊都已經知道,不需要再協商子協定。</summary>
    private static async Task ClientUpgradeHandshakeAsync(Stream stream, string host, CancellationToken ct)
    {
        var keyBytes = new byte[16];
        RandomNumberGenerator.Fill(keyBytes);
        string key = Convert.ToBase64String(keyBytes);

        string request =
            "GET /rdpb HTTP/1.1\r\n" +
            $"Host: {host}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var headers = await ReadHttpHeadersAsync(stream, ct).ConfigureAwait(false);
        if (headers.Count == 0 || !headers[0].Contains("101"))
            throw new IOException($"WebSocket Upgrade 失敗,對面沒有回 101 Switching Protocols:{(headers.Count > 0 ? headers[0] : "(空回應)")}");

        string expectedAccept = ComputeAcceptKey(key);
        bool found = headers.Any(h =>
            h.StartsWith("Sec-WebSocket-Accept:", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(h["Sec-WebSocket-Accept:".Length..].Trim(), expectedAccept, StringComparison.Ordinal));
        if (!found)
            throw new IOException("WebSocket Upgrade 失敗:Sec-WebSocket-Accept 跟預期的雜湊不符,連線可能被動過");
    }

    /// <summary>Server 端 HTTP Upgrade 交握。驗證要求本身是合法的 WS upgrade,不檢查
    /// Host/Origin 之類的欄位——這條連線的信任建立在後面的 mTLS 憑證指紋配對(計畫 §2),
    /// 不是這一層 HTTP header。</summary>
    internal static async Task<string> ServerUpgradeHandshakeAsync(Stream stream, CancellationToken ct)
    {
        var headers = await ReadHttpHeadersAsync(stream, ct).ConfigureAwait(false);
        if (headers.Count == 0 || !headers[0].StartsWith("GET", StringComparison.OrdinalIgnoreCase))
            throw new IOException("WebSocket Upgrade 失敗:不是合法的 HTTP GET 請求");

        string? key = headers
            .FirstOrDefault(h => h.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            ?["Sec-WebSocket-Key:".Length..].Trim();
        if (string.IsNullOrEmpty(key))
            throw new IOException("WebSocket Upgrade 失敗:請求裡沒有 Sec-WebSocket-Key");

        string accept = ComputeAcceptKey(key);
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        return key;
    }

    private static string ComputeAcceptKey(string clientKey) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(clientKey + WebSocketGuid)));

    /// <summary>一行一行讀 HTTP header,讀到空行為止,逐 byte 讀(訊息很短,幾行而已,
    /// 不值得為此另外拉一個有緩衝的 reader——重點是絕對不能多讀過頭,讀到 header 以外的
    /// bytes,那些是接下來 WebSocket 要用的資料,多讀一個 byte 這個 stream 之後的 WS
    /// framing 就全部對不上了。</summary>
    private static async Task<List<string>> ReadHttpHeadersAsync(Stream stream, CancellationToken ct)
    {
        var lines = new List<string>();
        var current = new List<byte>();
        var oneByte = new byte[1];

        while (true)
        {
            int n = await stream.ReadAsync(oneByte.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("WebSocket Upgrade 交握中連線被對面關閉");

            if (oneByte[0] == '\n')
            {
                string line = Encoding.ASCII.GetString(current.ToArray()).TrimEnd('\r');
                if (line.Length == 0) break; // header 區段結束的空行
                lines.Add(line);
                current.Clear();
            }
            else
            {
                current.Add(oneByte[0]);
            }
        }

        return lines;
    }
}
