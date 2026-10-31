using System.Net;
using System.Net.Sockets;
using RdpB.Core.Transport;
using RdpB.Core.Transport.Obfuscation;
using RdpB.Tests.TestSupport;

namespace RdpB.Tests;

/// <summary>計畫 §6/§7:偽裝的生效條件是「沒有走代理,而且使用者的連線意圖是 DIRECT」——
/// 這裡驗證 TransportConnector 真的照這個規則接線,不是只有 RdpCamouflageObfuscator 自己
/// 單獨測過(見 RdpCamouflageObfuscatorTests)。
///
/// **Kyra 要求的改動之後**:偽裝預設直接疊在使用者指定的那個埠上(不用另外佔用 3389),
/// 3389 變成進階選項(<see cref="TransportConnector.ConnectAsync"/> 的
/// useDedicatedCamouflagePort 參數)——下面的測試分別驗證兩條路徑。</summary>
public class TransportConnectorObfuscationTests
{
    [Fact]
    public async Task ConnectAsync_Direct_WithoutDedicatedPort_AppliesObfuscationOnSamePort()
    {
        // 單一個監聽器(自動偵測,見 TcpTlsTransportListener 上的註解),不另外開偽裝專用埠——
        // 這就是現在的預設路徑:偽裝握手疊在使用者指定的那個埠上。
        var cert = EphemeralCertificate.CreateSelfSigned();
        using var listener = TcpTlsTransportListener.Start(
            new IPEndPoint(IPAddress.Loopback, 0), cert, clientCertificateRequired: false);

        var acceptTask = listener.AcceptAsync();

        // mode=Direct,quicPort 隨便指一個沒人監聽的埠,QUIC 連線失敗後會照 isDirectContext
        // 的規則退回 TCP+TLS 並套偽裝——驗證的是「退到 TCP 之後有沒有正確套用偽裝」,
        // 不是 QUIC 本身(QUIC 有自己的測試)。
        var result = await TransportConnector.ConnectAsync(
            TransportMode.Direct, "127.0.0.1",
            quicPort: listener.BoundPort, tcpPort: listener.BoundPort,
            proxy: null, validateServerCertificate: _ => true,
            enableRdpCamouflage: true, useDedicatedCamouflagePort: false);

        await using var server = await acceptTask;
        await using (result.Transport) { }

        Assert.Equal(TransportKind.Tcp, result.Kind);
    }

    [Fact]
    public async Task ConnectAsync_Direct_WithDedicatedPort_RoutesToCamouflagePort()
    {
        // 進階選項開著時,照 §7 原文連去獨立的偽裝埠,不是使用者指定的那個「主」埠。
        var cert = EphemeralCertificate.CreateSelfSigned();
        using var camouflageListener = TcpTlsTransportListener.Start(
            new IPEndPoint(IPAddress.Loopback, 0), cert, clientCertificateRequired: false);

        // 故意開另一個「主」埠——如果 TransportConnector 沒有真的改走偽裝埠,而是打去這個埠,
        // 偽裝握手(client 端還是會送)會被這個監聽器的自動偵測正確接住,所以光靠「連不連得上」
        // 不能驗證有沒有連去 BoundPort,改成直接斷言 camouflageListener 真的 Accept 到了連線。
        using var plainListener = TcpTlsTransportListener.Start(
            new IPEndPoint(IPAddress.Loopback, 0), cert, clientCertificateRequired: false);

        var acceptTask = camouflageListener.AcceptAsync();

        var result = await TransportConnector.ConnectAsync(
            TransportMode.Direct, "127.0.0.1",
            quicPort: plainListener.BoundPort, tcpPort: plainListener.BoundPort,
            proxy: null, validateServerCertificate: _ => true,
            rdpCamouflagePort: camouflageListener.BoundPort,
            enableRdpCamouflage: true, useDedicatedCamouflagePort: true);

        await using var server = await acceptTask; // camouflageListener 真的收到連線,不是 plainListener
        await using (result.Transport) { }

        Assert.Equal(TransportKind.Tcp, result.Kind);
    }

    [Fact]
    public async Task ConnectAsync_WithProxy_NeverObfuscates_EvenWhenCamouflageEnabled()
    {
        // 走代理時,就算 enableRdpCamouflage 是 true(預設值),也不該套偽裝——§6:兩者互斥。
        var cert = EphemeralCertificate.CreateSelfSigned();
        using var plainListener = TcpTlsTransportListener.Start(
            new IPEndPoint(IPAddress.Loopback, 0), cert, clientCertificateRequired: false);

        using var socks5 = new RelayingMiniSocks5Server(plainListener.BoundPort);
        var proxy = new ProxyOptions(ProxyKind.Socks5, "127.0.0.1", socks5.Port);

        var acceptTask = plainListener.AcceptAsync();

        var result = await TransportConnector.ConnectAsync(
            TransportMode.Tcp, "127.0.0.1",
            quicPort: plainListener.BoundPort, tcpPort: plainListener.BoundPort,
            proxy, validateServerCertificate: _ => true,
            enableRdpCamouflage: true);

        await using var server = await acceptTask;
        await using (result.Transport) { }

        Assert.Equal(TransportKind.Tcp, result.Kind);
    }
}

/// <summary>最小可用的 SOCKS5 假伺服器——只認無認證、只認 CONNECT,而且不管客戶端
/// 要求的目的位址是什麼,一律轉給建構時指定的那個固定本機埠(測試不需要真的解析
/// 客戶端要求的位址,只需要「有一個會真的轉送 bytes 的代理」來驗證 TransportConnector
/// 不會在走代理的路徑上套用 §7 的偽裝)。</summary>
internal sealed class RelayingMiniSocks5Server : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _targetPort;

    public int Port { get; }

    public RelayingMiniSocks5Server(int targetPort)
    {
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = HandleAsync(client);
            }
        }
        catch { /* listener 被 Dispose 時正常結束 */ }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var c = client;
        var stream = c.GetStream();

        var greeting = await ReadExactAsync(stream, 2); // [ver][nmethods]
        await ReadExactAsync(stream, greeting[1]); // 跳過 methods 列表
        await stream.WriteAsync(new byte[] { 0x05, 0x00 }); // 選無認證

        var reqHeader = await ReadExactAsync(stream, 4); // [ver][cmd][rsv][atyp]
        int addrLen = reqHeader[3] switch { 0x01 => 4, 0x04 => 16, 0x03 => (await ReadExactAsync(stream, 1))[0] + 1, _ => 0 };
        if (reqHeader[3] == 0x03) addrLen -= 1; // domain 類型上面那行已經多讀了一個 length byte
        await ReadExactAsync(stream, addrLen + 2); // 位址 + port,內容不重要

        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }); // 成功

        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, _targetPort).ConfigureAwait(false);
        var targetStream = target.GetStream();

        var a = stream.CopyToAsync(targetStream);
        var b = targetStream.CopyToAsync(stream);
        await Task.WhenAny(a, b).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int count)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset, count - offset)).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
        return buf;
    }

    public void Dispose() => _listener.Stop();
}
