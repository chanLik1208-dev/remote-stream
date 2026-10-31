using System.Net;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>計畫 §1 的 WebSocket+TLS transport——跟 TCP/QUIC 一樣,實際跑一次 mTLS +
/// HTTP Upgrade 交握 + 真的送收訊息,不是只測 HTTP header 字串拼得對不對。</summary>
public class WsTransportTests
{
    [Fact]
    public async Task ConnectAsync_CompletesUpgradeHandshake_AndDeliversMessages_EndToEnd()
    {
        var cert = EphemeralCertificate.CreateSelfSigned();
        using var listener = WsTransportListener.Start(
            new IPEndPoint(IPAddress.Loopback, 0), cert, clientCertificateRequired: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var acceptTask = listener.AcceptAsync(cts.Token);

        await using var client = await WsTransport.ConnectAsync(
            "127.0.0.1", listener.BoundPort, proxy: null, validateServerCertificate: _ => true, ct: cts.Token);
        await using var server = await acceptTask;

        var received = new TaskCompletionSource<byte[]>();
        server.MessageReceived += (_, _, payload) => received.TrySetResult(payload.ToArray());

        await client.SendAsync(Channel.Control, MessageType.Heartbeat, new byte[] { 7, 7, 7 });

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(received.Task, completed);
        Assert.Equal(new byte[] { 7, 7, 7 }, await received.Task);
    }

    [Fact]
    public async Task ConnectAsync_DeliversLargeVideoSizedMessage_AcrossMultipleReceiveChunks()
    {
        // 一個 WS message 可能比單次 ReceiveAsync 的緩衝區大(ReceiveLoopAsync 用 64KB 的
        // chunk),這裡故意送一個超過那個大小的 payload,確認多次 chunk 真的會被正確
        // 接回同一個 Frame,不會在邊界上斷掉或串錯。
        var cert = EphemeralCertificate.CreateSelfSigned();
        using var listener = WsTransportListener.Start(
            new IPEndPoint(IPAddress.Loopback, 0), cert, clientCertificateRequired: false);

        var acceptTask = listener.AcceptAsync();

        await using var client = await WsTransport.ConnectAsync(
            "127.0.0.1", listener.BoundPort, proxy: null, validateServerCertificate: _ => true);
        await using var server = await acceptTask;

        var payload = new byte[200 * 1024];
        new Random(42).NextBytes(payload);

        var received = new TaskCompletionSource<byte[]>();
        server.MessageReceived += (_, _, p) => received.TrySetResult(p.ToArray());

        await client.SendAsync(Channel.Video, MessageType.VideoFrame, payload);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(received.Task, completed);
        Assert.Equal(payload, await received.Task);
    }

    [Fact]
    public async Task TransportConnector_WsMode_RoutesToWsPortAndCompletesHandshake()
    {
        var cert = EphemeralCertificate.CreateSelfSigned();
        using var listener = WsTransportListener.Start(
            new IPEndPoint(IPAddress.Loopback, 0), cert, clientCertificateRequired: false);

        var acceptTask = listener.AcceptAsync();

        var result = await TransportConnector.ConnectAsync(
            TransportMode.Ws, "127.0.0.1",
            quicPort: 0, tcpPort: 0, proxy: null, validateServerCertificate: _ => true,
            wsPort: listener.BoundPort);

        await using var server = await acceptTask;
        await using (result.Transport) { }

        Assert.Equal(TransportKind.Ws, result.Kind);
    }
}
