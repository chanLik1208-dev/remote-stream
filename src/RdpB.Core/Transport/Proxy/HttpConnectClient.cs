using System.Text;

namespace RdpB.Core.Transport.Proxy;

/// <summary>
/// 極簡 HTTP CONNECT handshake,給走 WebSocket+TLS 時在企業代理/某些 CDN 後面用。
/// </summary>
public static class HttpConnectClient
{
    public static async Task ConnectAsync(
        Stream stream,
        string targetHost,
        int targetPort,
        string? proxyAuthorizationHeader = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append($"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n");
        sb.Append($"Host: {targetHost}:{targetPort}\r\n");
        if (proxyAuthorizationHeader is not null)
            sb.Append($"Proxy-Authorization: {proxyAuthorizationHeader}\r\n");
        sb.Append("Proxy-Connection: Keep-Alive\r\n\r\n");

        var reqBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(reqBytes, ct).ConfigureAwait(false);

        string statusLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
        // 讀掉剩下的 header,直到空行
        string line;
        do
        {
            line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
        } while (line.Length > 0);

        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out int statusCode))
            throw new IOException($"HTTP CONNECT:無法解析狀態行「{statusLine}」");

        if (statusCode is < 200 or >= 300)
            throw new IOException($"HTTP CONNECT 失敗:{statusLine}");
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("HTTP CONNECT:連線在 handshake 中被對面關閉");

            char c = (char)buf[0];
            if (c == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                    sb.Length--;
                return sb.ToString();
            }
            sb.Append(c);
        }
    }
}
