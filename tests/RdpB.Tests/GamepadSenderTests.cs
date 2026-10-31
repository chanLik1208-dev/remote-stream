using RdpB.Client.Input;

namespace RdpB.Tests;

/// <summary>計畫 §9/§5「手把」Client 端那一半(見 GamepadSender 類別上的註解,Host 端
/// GamepadSink 早就做完了,這次補上 Client 端讀取/送出)。GamepadSink 需要真的裝
/// ViGEmBus 驅動+虛擬手把才能跑,這裡不重跑那一段,只驗證 GamepadSender.BuildPayload
/// 產生的 12-byte 版面,跟 GamepadSink.Handle 的解析邏輯(手動抄一份同樣的解析方式)
/// 對得起來——這才是兩端真正會踩雷的地方(位元組順序、哪個 byte 放哪個欄位)。</summary>
public class GamepadSenderTests
{
    [Fact]
    public void BuildPayload_RoundTripsThroughGamepadSinkStyleParsing()
    {
        ushort buttons = 0x1234;
        byte leftTrigger = 0x56, rightTrigger = 0x78;
        short lx = -12345, ly = 12345, rx = short.MinValue, ry = short.MaxValue;

        var payload = GamepadSender.BuildPayload(buttons, leftTrigger, rightTrigger, lx, ly, rx, ry);

        Assert.Equal(12, payload.Length);

        // 跟 RdpB.Host.Input.GamepadSink.Handle 完全一樣的解析方式(小端),
        // 不直接參照那個型別——那個專案需要 ViGEmBus 原生依賴,測試不需要真的連過去。
        ushort parsedButtons = (ushort)(payload[0] | (payload[1] << 8));
        byte parsedLeftTrigger = payload[2];
        byte parsedRightTrigger = payload[3];
        short parsedLx = ReadI16(payload, 4);
        short parsedLy = ReadI16(payload, 6);
        short parsedRx = ReadI16(payload, 8);
        short parsedRy = ReadI16(payload, 10);

        Assert.Equal(buttons, parsedButtons);
        Assert.Equal(leftTrigger, parsedLeftTrigger);
        Assert.Equal(rightTrigger, parsedRightTrigger);
        Assert.Equal(lx, parsedLx);
        Assert.Equal(ly, parsedLy);
        Assert.Equal(rx, parsedRx);
        Assert.Equal(ry, parsedRy);
    }

    private static short ReadI16(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8));
}
