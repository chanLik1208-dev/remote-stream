using System.Buffers.Binary;

namespace RdpB.Core.Protocol;

/// <summary>
/// 一個 Session frame 的線路格式:
///   [1 byte  Channel]
///   [1 byte  MessageType]
///   [4 bytes PayloadLength, little-endian]
///   [PayloadLength bytes Payload]
/// 這是 TCP/WebSocket transport 用的自訂 frame header(§1)。
/// QUIC transport 不需要這層——每個訊息走自己的 stream,stream 本身就有邊界。
/// </summary>
public readonly struct Frame
{
    public const int HeaderSize = 6;

    public Channel Channel { get; }
    public MessageType Type { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public Frame(Channel channel, MessageType type, ReadOnlyMemory<byte> payload)
    {
        Channel = channel;
        Type = type;
        Payload = payload;
    }

    public int WireSize => HeaderSize + Payload.Length;

    /// <summary>把這個 frame 寫進目的緩衝區,回傳實際寫入的 byte 數。緩衝區至少要有 WireSize。</summary>
    public int WriteTo(Span<byte> dest)
    {
        if (dest.Length < WireSize)
            throw new ArgumentException("目的緩衝區太小", nameof(dest));

        WriteHeaderTo(dest);
        Payload.Span.CopyTo(dest[HeaderSize..]);
        return WireSize;
    }

    /// <summary>只寫 6 byte 的 header(channel/type/payload 長度),不需要緩衝區能裝下 payload。
    /// 給「header 和 payload 分兩次 WriteAsync 送出」的呼叫端用(FrameStreamPump、QuicTransport 的
    /// 單向 stream 送法都是這樣,沒必要先把兩者併成一塊連續記憶體)。</summary>
    public void WriteHeaderTo(Span<byte> dest)
    {
        if (dest.Length < HeaderSize)
            throw new ArgumentException("目的緩衝區太小", nameof(dest));

        dest[0] = (byte)Channel;
        dest[1] = (byte)Type;
        BinaryPrimitives.WriteInt32LittleEndian(dest[2..6], Payload.Length);
    }

    public byte[] ToBytes()
    {
        var buf = new byte[WireSize];
        WriteTo(buf);
        return buf;
    }

    /// <summary>嘗試從 buffer 開頭解析出一個完整的 frame。
    /// 回傳 true 且 consumed 是這個 frame 佔用的 byte 數;資料不夠就回傳 false、consumed = 0。</summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out Frame frame, out int consumed)
    {
        frame = default;
        consumed = 0;

        if (buffer.Length < HeaderSize)
            return false;

        var channel = (Channel)buffer[0];
        var type = (MessageType)buffer[1];
        int len = BinaryPrimitives.ReadInt32LittleEndian(buffer[2..6]);

        if (len < 0)
            throw new InvalidDataException($"frame 長度不合法:{len}");

        if (buffer.Length < HeaderSize + len)
            return false; // 資料還沒收齊

        var payload = buffer.Slice(HeaderSize, len).ToArray();
        frame = new Frame(channel, type, payload);
        consumed = HeaderSize + len;
        return true;
    }
}
