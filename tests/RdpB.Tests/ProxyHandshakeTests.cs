using System.Net;
using System.Net.Sockets;
using System.Text;
using RdpB.Core.Transport.Proxy;

namespace RdpB.Tests;

/// <summary>
/// 對應計畫驗證清單:「SOCKS5/HTTP CONNECT 握手(測試裡起一個迷你 SOCKS5 server)」。
/// 這兩個迷你 server 只做到能讓 handshake 跑完所需的最小行為,不是真的轉發流量。
/// </summary>
public class ProxyHandshakeTests
{
    [Fact]
    public async Task Socks5Client_NoAuth_CompletesHandshake_AgainstMiniServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            using var s = server.GetStream();

            // ① 方法協商:期待 ver=5, nmethods=1, no-auth
            var greeting = await ReadExactAsync(s, 3);
            Assert.Equal(0x05, greeting[0]);
            await s.WriteAsync(new byte[] { 0x05, 0x00 }); // 選 no-auth

            // ② CONNECT 請求(domain atyp)
            var head = await ReadExactAsync(s, 5); // ver,cmd,rsv,atyp,domainLen
            int domainLen = head[4];
            await ReadExactAsync(s, domainLen + 2); // domain + port

            // ③ 回覆成功,bnd 用 IPv4 隨便填
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await Socks5Client.ConnectAsync(client.GetStream(), "example.internal", 1234);

        await serverTask; // 沒有例外就算通過
    }

    [Fact]
    public async Task Socks5Client_WithAuth_CompletesHandshake_AgainstMiniServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            using var s = server.GetStream();

            var greeting = await ReadExactAsync(s, 4); // ver, nmethods=2, no-auth, user/pass
            Assert.Contains((byte)0x02, new[] { greeting[2], greeting[3] });
            await s.WriteAsync(new byte[] { 0x05, 0x02 }); // 選 user/pass

            var authHead = await ReadExactAsync(s, 2); // ver, ulen
            int ulen = authHead[1];
            var user = await ReadExactAsync(s, ulen);
            var plenBuf = await ReadExactAsync(s, 1);
            var pass = await ReadExactAsync(s, plenBuf[0]);
            Assert.Equal("kyra", Encoding.UTF8.GetString(user));
            Assert.Equal("secret", Encoding.UTF8.GetString(pass));
            await s.WriteAsync(new byte[] { 0x01, 0x00 }); // 認證成功

            var head = await ReadExactAsync(s, 5);
            int domainLen = head[4];
            await ReadExactAsync(s, domainLen + 2);
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await Socks5Client.ConnectAsync(client.GetStream(), "example.internal", 1234,
            new Socks5Credentials("kyra", "secret"));

        await serverTask;
    }

    [Fact]
    public async Task Socks5Client_ServerRejects_ThrowsWithReason()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            using var s = server.GetStream();
            await ReadExactAsync(s, 3);
            await s.WriteAsync(new byte[] { 0x05, 0x00 });
            var head = await ReadExactAsync(s, 5);
            await ReadExactAsync(s, head[4] + 2);
            await s.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }); // 0x05 = 連線被拒絕
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            Socks5Client.ConnectAsync(client.GetStream(), "example.internal", 1234));
        Assert.Contains("連線被拒絕", ex.Message);

        await serverTask;
    }

    [Fact]
    public async Task HttpConnectClient_CompletesHandshake_AgainstMiniServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            using var s = server.GetStream();
            using var reader = new StreamReader(s, Encoding.ASCII, leaveOpen: true);

            string? line;
            bool sawConnect = false;
            while ((line = await reader.ReadLineAsync()) is not null && line.Length > 0)
            {
                if (line.StartsWith("CONNECT")) sawConnect = true;
            }
            Assert.True(sawConnect);

            var replyBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
            await s.WriteAsync(replyBytes);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await HttpConnectClient.ConnectAsync(client.GetStream(), "example.internal", 1234);

        await serverTask;
    }

    [Fact]
    public async Task HttpConnectClient_NonSuccessStatus_Throws()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            using var s = server.GetStream();
            using var reader = new StreamReader(s, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync() is { Length: > 0 }) { }

            var replyBytes = Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n");
            await s.WriteAsync(replyBytes);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        await Assert.ThrowsAsync<IOException>(() =>
            HttpConnectClient.ConnectAsync(client.GetStream(), "example.internal", 1234));

        await serverTask;
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int count)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset, count - offset));
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
        return buf;
    }
}
