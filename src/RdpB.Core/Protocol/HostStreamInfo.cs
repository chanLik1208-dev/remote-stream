using System.Text;
using System.Text.Json;

namespace RdpB.Core.Protocol;

/// <summary>計畫 §4/§8 HUD「協議/編解碼細節」那一塊要顯示的東西裡,只有 Host 端才知道的
/// 部分(用什麼編碼器、目標 fps/位元率)——Client 自己的解碼器種類、實際畫面解析度,
/// Client 自己量得到,不需要 Host 再講一次。Host 開始串流後透過 <see cref="MessageType.ConfigSync"/>
/// (這個 MessageType 存在很久了,原本完全沒用到,見 docs/PROTOCOL.md 第 3 節的已知缺口)
/// 送一次給 Client。
///
/// **用 JSON,不是手刻二進位格式**:這個方案裡大部分訊息(Pair/PairAck)為了效能/體積手刻
/// 固定版面的二進位格式,但 ConfigSync 整個連線只送一次(串流開始時),不在熱路徑上,體積
/// 大個幾十 bytes 完全無感——用 <see cref="JsonSerializer"/> 換取好讀好改好擴充,比為了
/// 一次性訊息去手刻、手維護一份二進位版面划算。</summary>
public sealed record HostStreamInfo(string Encoder, int TargetFps, int TargetBitrateKbps)
{
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));

    public static bool TryParse(ReadOnlySpan<byte> payload, out HostStreamInfo? info)
    {
        try
        {
            info = JsonSerializer.Deserialize<HostStreamInfo>(payload);
            return info is not null;
        }
        catch (JsonException)
        {
            // 對面送壞掉的/不相容版本的 payload 不該讓整個 Session 死掉——這只是 HUD 顯示用
            // 的錦上添花資訊,解析失敗就當作沒收到,HUD 那幾個欄位維持「量測中…」。
            info = null;
            return false;
        }
    }
}
