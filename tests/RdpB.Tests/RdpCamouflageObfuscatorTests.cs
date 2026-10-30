using System.Net;
using System.Net.Sockets;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Core.Transport.Obfuscation;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

public class RdpCamouflageObfuscatorTests
{
    [Fact]
    public void ConnectionRequest_LooksLikeRealRdp_TpktAndX224Header()
    {
        var bytes = RdpCamouflageObfuscator.BuildConnectionRequest();

        Assert.Equal(0x03, bytes[0]); // TPKT version
        Assert.Equal(0x00, bytes[1]); // TPKT reserved
        int declaredLen = (bytes[2] << 8) | bytes[3];
        Assert.Equal(bytes.Length, declaredLen); // TPKT 宣告的長度要跟實際送出的一致

        Assert.Equal(0xE0, bytes[5]); // X.224 CR TPDU code
        Assert.Contains("mstshash", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void ConnectionConfirm_LooksLikeRealRdp_TpktAndX224Header()
    {
        var bytes = RdpCamouflageObfuscator.BuildConnectionConfirm();

        Assert.Equal(0x03, bytes[0]);
        int declaredLen = (bytes[2] << 8) | bytes[3];
        Assert.Equal(bytes.Length, declaredLen);
        Assert.Equal(0xD0, bytes[5]); // X.224 CC TPDU code
    }

    [Fact]
    public async Task ClientAndServerHandshake_CompleteSuccessfully_AgainstEachOther()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var obfuscator = new RdpCamouflageObfuscator();

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await obfuscator.ServerHandshakeAsync(server.GetStream(), CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await obfuscator.ClientHandshakeAsync(client.GetStream(), CancellationToken.None);

        await serverTask; // 沒有例外就算通過
    }

    [Fact]
    public async Task TcpTlsTransport_WithRdpObfuscator_StillDeliversMessages_EndToEnd()
    {
        var cert = EphemeralCertificate.CreateSelfSigned();
        using var listener = TcpTlsTransportListener.Start(
            new IPEndPoint(IPAddress.Loopback, 0), cert,
            obfuscator: new RdpCamouflageObfuscator(), clientCertificateRequired: false);

        var acceptTask = listener.AcceptAsync();

        await using var client = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", listener.BoundPort, proxy: null, validateServerCertificate: _ => true,
            obfuscator: new RdpCamouflageObfuscator());
        await using var server = await acceptTask;

        var received = new TaskCompletionSource<byte[]>();
        server.MessageReceived += (_, _, payload) => received.TrySetResult(payload.ToArray());

        await client.SendAsync(Channel.Control, MessageType.Heartbeat, new byte[] { 9, 9 });

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(received.Task, completed);
        Assert.Equal(new byte[] { 9, 9 }, await received.Task);
    }
}
