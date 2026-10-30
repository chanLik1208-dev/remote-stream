using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RdpB.Core.Transport.Proxy;

public sealed record Socks5Credentials(string Username, string Password);

/// <summary>
/// 極簡 SOCKS5 client handshake(RFC 1928/1929)。只實作 CONNECT,夠用來接
/// v2rayN/Clash 本機的 SOCKS5 port,或任何標準 SOCKS5 代理。不依賴任何第三方套件。
/// </summary>
public static class Socks5Client
{
    /// <summary>
    /// 在已經連上 proxy 的 <paramref name="stream"/> 上執行 SOCKS5 handshake,
    /// 要求 proxy 幫忙連到 <paramref name="targetHost"/>:<paramref name="targetPort"/>。
    /// Handshake 成功後,這條 stream 之後的讀寫就等同直接連到目標。
    /// </summary>
    public static async Task ConnectAsync(
        Stream stream,
        string targetHost,
        int targetPort,
        Socks5Credentials? credentials = null,
        CancellationToken ct = default)
    {
        // ① 方法協商
        bool withAuth = credentials is not null;
        byte[] greeting = withAuth
            ? [0x05, 0x02, 0x00, 0x02] // ver, nmethods, no-auth, user/pass
            : [0x05, 0x01, 0x00];
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);

        var methodReply = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
        if (methodReply[0] != 0x05)
            throw new IOException("SOCKS5:伺服器回覆的版本不對");

        byte chosen = methodReply[1];
        if (chosen == 0xFF)
            throw new IOException("SOCKS5:伺服器拒絕所有認證方式");

        // ② 如果選了 user/pass,走一次認證子協商(RFC 1929)
        if (chosen == 0x02)
        {
            if (credentials is null)
                throw new IOException("SOCKS5:伺服器要求帳密認證,但沒有提供");

            var userBytes = Encoding.UTF8.GetBytes(credentials.Username);
            var passBytes = Encoding.UTF8.GetBytes(credentials.Password);
            var authReq = new byte[3 + userBytes.Length + passBytes.Length];
            authReq[0] = 0x01;
            authReq[1] = (byte)userBytes.Length;
            userBytes.CopyTo(authReq, 2);
            authReq[2 + userBytes.Length] = (byte)passBytes.Length;
            passBytes.CopyTo(authReq, 3 + userBytes.Length);

            await stream.WriteAsync(authReq, ct).ConfigureAwait(false);
            var authReply = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
            if (authReply[1] != 0x00)
                throw new IOException("SOCKS5:帳密認證失敗");
        }

        // ③ CONNECT 請求。優先用域名(ATYP=0x03),代理自己去解析,避免 DNS 外洩。
        var hostBytes = Encoding.ASCII.GetBytes(targetHost);
        if (hostBytes.Length > 255)
            throw new ArgumentException("目標主機名太長", nameof(targetHost));

        var req = new byte[4 + 1 + hostBytes.Length + 2];
        req[0] = 0x05; // ver
        req[1] = 0x01; // cmd = CONNECT
        req[2] = 0x00; // rsv
        req[3] = 0x03; // atyp = domain name
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        req[^2] = (byte)(targetPort >> 8);
        req[^1] = (byte)(targetPort & 0xFF);

        await stream.WriteAsync(req, ct).ConfigureAwait(false);

        // ④ 回覆:ver, rep, rsv, atyp, (bnd.addr 長度依 atyp 不同), bnd.port(2 bytes)
        var replyHead = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
        if (replyHead[0] != 0x05)
            throw new IOException("SOCKS5:回覆版本不對");
        if (replyHead[1] != 0x00)
            throw new IOException($"SOCKS5:CONNECT 失敗,錯誤碼 {replyHead[1]}({DescribeError(replyHead[1])})");

        int addrLen = replyHead[3] switch
        {
            0x01 => 4,   // IPv4
            0x03 => (await ReadExactAsync(stream, 1, ct).ConfigureAwait(false))[0], // domain,先讀長度 byte
            0x04 => 16,  // IPv6
            _ => throw new IOException("SOCKS5:未知的 ATYP"),
        };
        await ReadExactAsync(stream, addrLen + 2, ct).ConfigureAwait(false); // bnd.addr + bnd.port,丟棄不用
    }

    private static string DescribeError(byte code) => code switch
    {
        0x01 => "一般性伺服器錯誤",
        0x02 => "規則不允許連線",
        0x03 => "網路無法到達",
        0x04 => "主機無法到達",
        0x05 => "連線被拒絕",
        0x06 => "TTL 逾時",
        0x07 => "不支援的指令",
        0x08 => "不支援的位址類型",
        _ => "未知錯誤",
    };

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("SOCKS5:連線在 handshake 中被對面關閉");
            offset += n;
        }
        return buf;
    }
}
