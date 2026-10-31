using System.Text;

namespace RdpB.Core.Transport.Obfuscation;

/// <summary>
/// 計畫 §7:只在 DIRECT 模式生效的連線外觀偽裝。TCP 連上之後,先交換一組跟真實 RDP
/// 一模一樣外形的 TPKT/X.224 Connection Request / Connection Confirm(MS-RDPBCGR 公開規格),
/// 用 RDP_NEG_REQ/RDP_NEG_RSP 宣告要切到 TLS 安全層——這正好跟真實 RDP 切進 TLS 的時機吻合,
/// 之後雙方就直接開始 TLS handshake,跟真的 RDP 連線在這一步完全對不上差別。
///
/// 這不是在「相容」RDP(不會真的跑 RDP 的後續流程),純粹是讓 DPI/防火牆在這幾個位元組上
/// 看不出跟一般 RDP 工作階段的差異。擋得住多數只看埠和握手開頭的被動 DPI,擋不住會完整
/// 模擬一次 RDP 流程的主動探測——這點計畫裡已經講清楚,不誇大效果。
/// </summary>
public sealed class RdpCamouflageObfuscator : IObfuscator
{
    public string Name => "rdp";

    /// <summary>TPKT header 的第一個 byte 固定是版本號 3——跟任何合法的 TLS record(第一個
    /// byte 是 content type,20~23 之間)完全不會混到,<see cref="TcpTlsTransportListener"/>
    /// 用這個來自動判斷一個進來的連線有沒有在套 RDP 偽裝的握手,不用另外約定埠號才能分辨
    /// (見 Kyra 要求的改動:偽裝的核心價值是握手外觀,不是硬要佔用 3389 這個埠號本身)。</summary>
    public const byte TpktVersionByte = 0x03;

    // PROTOCOL_SSL,RDP_NEG_REQ/RSP 的 requestedProtocols/selectedProtocol 都用這個值,
    // 表示「協商完就切 TLS」——這是真實 RDP 用來宣告走 TLS 安全層的標準值。
    private const uint ProtocolSsl = 0x00000001;

    public async Task ClientHandshakeAsync(Stream stream, CancellationToken ct)
    {
        var request = BuildConnectionRequest();
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        // 讀掉對面的 Connection Confirm,不需要真的解析內容——TLS handshake 接下來自己會
        // 驗證身分,這裡只是把偽裝用的位元組從 stream 讀乾淨,不留在緩衝區裡干擾 SslStream。
        await ReadOneTpktFrameAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task ServerHandshakeAsync(Stream stream, CancellationToken ct)
    {
        await ReadOneTpktFrameAsync(stream, ct).ConfigureAwait(false);

        var confirm = BuildConnectionConfirm();
        await stream.WriteAsync(confirm, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Client → Host:TPKT + X.224 Connection Request(CR,0xE0)+ cookie + RDP_NEG_REQ。</summary>
    public static byte[] BuildConnectionRequest()
    {
        var cookie = Encoding.ASCII.GetBytes("Cookie: mstshash=RDPB\r\n");
        var negReq = BuildNegotiationBlock(type: 0x01, protocol: ProtocolSsl);
        var variable = Concat(cookie, negReq);

        // X.224 CR 固定部分:CDT/CR code(1)+ DST-REF(2) + SRC-REF(2) + CLASS(1) = 6 bytes
        byte[] x224Fixed = [0xE0, 0x00, 0x00, 0x00, 0x00, 0x00];
        return BuildTpktFrame(x224Fixed, variable);
    }

    /// <summary>Host → Client:TPKT + X.224 Connection Confirm(CC,0xD0)+ RDP_NEG_RSP。</summary>
    public static byte[] BuildConnectionConfirm()
    {
        var negRsp = BuildNegotiationBlock(type: 0x02, protocol: ProtocolSsl);
        byte[] x224Fixed = [0xD0, 0x00, 0x00, 0x00, 0x00, 0x00];
        return BuildTpktFrame(x224Fixed, negRsp);
    }

    private static byte[] BuildNegotiationBlock(byte type, uint protocol)
    {
        // RDP_NEG_REQ / RDP_NEG_RSP 共用的 8-byte 格式:type(1) flags(1) length=8(2,LE) protocol(4,LE)
        var block = new byte[8];
        block[0] = type;
        block[1] = 0x00; // flags
        block[2] = 0x08; block[3] = 0x00; // length = 8
        block[4] = (byte)(protocol & 0xFF);
        block[5] = (byte)((protocol >> 8) & 0xFF);
        block[6] = (byte)((protocol >> 16) & 0xFF);
        block[7] = (byte)((protocol >> 24) & 0xFF);
        return block;
    }

    private static byte[] BuildTpktFrame(byte[] x224Fixed, byte[] variable)
    {
        byte li = (byte)(x224Fixed.Length + variable.Length);
        var x224 = Concat([li], x224Fixed, variable);

        int tpktLen = 4 + x224.Length;
        byte[] tpktHeader = [0x03, 0x00, (byte)(tpktLen >> 8), (byte)(tpktLen & 0xFF)];
        return Concat(tpktHeader, x224);
    }

    /// <summary>讀一個完整的 TPKT frame(header 自帶長度,讀 header 就知道還要讀多少)。</summary>
    private static async Task<byte[]> ReadOneTpktFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
        if (header[0] != 0x03)
            throw new IOException("RDP 偽裝 handshake:對面送的開頭不是合法的 TPKT(version != 3),連線可能被動過");

        int totalLen = (header[2] << 8) | header[3];
        if (totalLen < 4 || totalLen > 4096)
            throw new IOException($"RDP 偽裝 handshake:TPKT 長度不合理({totalLen})");

        var rest = await ReadExactAsync(stream, totalLen - 4, ct).ConfigureAwait(false);
        return Concat(header, rest);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("RDP 偽裝 handshake 中連線被對面關閉");
            offset += n;
        }
        return buf;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }
}
