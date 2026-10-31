using RdpB.Core.Protocol;

namespace RdpB.Tests;

/// <summary>§8 HUD「協議/編解碼細節」區塊用的 Host→Client 一次性 ConfigSync payload——
/// 只驗證序列化往返正確、壞資料不會讓呼叫端整個炸掉(TryParse 的 false 路徑)。</summary>
public class HostStreamInfoTests
{
    [Fact]
    public void ToBytes_ThenTryParse_RoundTripsAllFields()
    {
        var original = new HostStreamInfo("h264_nvenc", 60, 15000);

        bool parsed = HostStreamInfo.TryParse(original.ToBytes(), out var result);

        Assert.True(parsed);
        Assert.Equal(original, result);
    }

    [Fact]
    public void TryParse_GarbagePayload_ReturnsFalseInsteadOfThrowing()
    {
        bool parsed = HostStreamInfo.TryParse("不是 JSON 也不是這個型別"u8, out var result);

        Assert.False(parsed);
        Assert.Null(result);
    }
}
