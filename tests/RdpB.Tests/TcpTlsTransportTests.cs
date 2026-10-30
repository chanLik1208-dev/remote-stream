using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>TcpTlsTransport 本身的連線/TLS/收送路徑——底下的 FrameStreamPump 已經有獨立測試,
/// 這裡只確認「連得起來、送得到」這條路沒接錯。</summary>
public class TcpTlsTransportTests
{
    [Fact]
    public async Task ConnectAndSend_DeliversPayload_ClientToServer()
    {
        var cert = EphemeralCertificate.CreateSelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            // 注意:raw 這個 TcpClient 不能用 `using` 提早 dispose——它要活到整個 transport
            // 結束為止,FromAcceptedStream 會把它收進去,由 transport 的 CloseAsync 負責關閉。
            var raw = await listener.AcceptTcpClientAsync();
            var ssl = new SslStream(raw.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(cert, clientCertificateRequired: false,
                checkCertificateRevocation: false);
            return TcpTlsTransport.FromAcceptedStream(ssl, raw);
        });

        await using var client = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", port, proxy: null, validateServerCertificate: _ => true);
        await using var server = await serverTask;

        var received = new TaskCompletionSource<(Channel, MessageType, byte[])>();
        server.MessageReceived += (ch, type, payload) =>
            received.TrySetResult((ch, type, payload.ToArray()));

        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        await client.SendAsync(Channel.Audio, MessageType.AudioFrame, payload);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(received.Task, completed);
        var (ch, type, actual) = await received.Task;

        Assert.Equal(Channel.Audio, ch);
        Assert.Equal(MessageType.AudioFrame, type);
        Assert.Equal(payload, actual);

        listener.Stop();
    }
}
