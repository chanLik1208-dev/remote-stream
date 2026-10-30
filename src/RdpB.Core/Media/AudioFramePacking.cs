namespace RdpB.Core.Media;

/// <summary>
/// 計畫 §9:音訊 payload 格式是「2 byte 序號(小端)+ Opus 編碼後的資料」。序號給
/// <see cref="AudioSequenceTracker"/> 判斷掉包用,Audio channel 可丟棄(計畫 §1),
/// 掉了也不會卡住,靠序號 + Opus PLC 補償。
/// </summary>
public static class AudioFramePacking
{
    public const int HeaderSize = 2;

    public static byte[] Pack(ushort seq, ReadOnlySpan<byte> opusData)
    {
        var buf = new byte[HeaderSize + opusData.Length];
        buf[0] = (byte)(seq & 0xff);
        buf[1] = (byte)(seq >> 8);
        opusData.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static bool TryUnpack(ReadOnlySpan<byte> payload, out ushort seq, out ReadOnlySpan<byte> opusData)
    {
        if (payload.Length < HeaderSize)
        {
            seq = 0;
            opusData = default;
            return false;
        }
        seq = (ushort)(payload[0] | (payload[1] << 8));
        opusData = payload[HeaderSize..];
        return true;
    }
}
