using RdpB.Core.Protocol;
using RdpB.Host.Input;

namespace RdpB.Tests;

internal sealed class FakeInputSink : IInputSink
{
    public List<string> Calls { get; } = new();

    public void SendKey(ushort scanCode, bool down, bool extended) => Calls.Add($"Key {scanCode} {down} {extended}");
    public void MouseAbs(ushort x, ushort y) => Calls.Add($"Abs {x} {y}");
    public void MouseRel(short dx, short dy) => Calls.Add($"Rel {dx} {dy}");
    public void MouseBtn(byte button, bool down) => Calls.Add($"Btn {button} {down}");
    public void Wheel(short delta, bool horizontal) => Calls.Add($"Wheel {delta} {horizontal}");
}

public class InjectorTests
{
    [Fact]
    public void Handle_Key_InjectsAndTracksState()
    {
        var sink = new FakeInputSink();
        var injector = new Injector(sink);

        injector.Handle(Channel.Input, MessageType.Key, [0x11, 0x00, 0x01]); // scanCode=0x11, down
        Assert.Contains("Key 17 True False", sink.Calls);
        Assert.Equal(1, injector.Keys.DownKeyCount);

        injector.Handle(Channel.Input, MessageType.Key, [0x11, 0x00, 0x00]); // up
        Assert.Equal(0, injector.Keys.DownKeyCount);
    }

    [Fact]
    public void Handle_MouseBtn_TracksState()
    {
        var sink = new FakeInputSink();
        var injector = new Injector(sink);

        injector.Handle(Channel.Input, MessageType.MouseBtn, [0, 1]); // left down
        Assert.Equal(1, injector.Keys.DownMouseButtonCount);

        injector.Handle(Channel.Input, MessageType.MouseBtn, [0, 0]); // left up
        Assert.Equal(0, injector.Keys.DownMouseButtonCount);
    }

    [Fact]
    public void ReleaseAll_SendsKeyUpForEveryHeldKeyAndButton_ThenClears()
    {
        var sink = new FakeInputSink();
        var injector = new Injector(sink);

        injector.Handle(Channel.Input, MessageType.Key, [0x11, 0x00, 0x01]);
        injector.Handle(Channel.Input, MessageType.Key, [0x2A, 0x00, 0x01]);
        injector.Handle(Channel.Input, MessageType.MouseBtn, [0, 1]);
        sink.Calls.Clear();

        injector.ReleaseAll();

        Assert.Contains("Key 17 False False", sink.Calls);
        Assert.Contains("Key 42 False False", sink.Calls);
        Assert.Contains("Btn 0 False", sink.Calls);
        Assert.Equal(0, injector.Keys.DownKeyCount);
        Assert.Equal(0, injector.Keys.DownMouseButtonCount);
    }

    [Fact]
    public void Handle_MouseRel_DoesNotTouchKeyState()
    {
        var sink = new FakeInputSink();
        var injector = new Injector(sink);

        injector.Handle(Channel.Input, MessageType.MouseRel, [10, 0, unchecked((byte)(-5)), 0xFF]);
        Assert.Contains("Rel 10 -5", sink.Calls); // dy 的 2 bytes 是 -5 的小端表示
        Assert.Equal(0, injector.Keys.DownKeyCount);
    }

    [Fact]
    public void Handle_TooShortPayload_IsIgnoredWithoutThrowing()
    {
        var sink = new FakeInputSink();
        var injector = new Injector(sink);

        injector.Handle(Channel.Input, MessageType.Key, [0x11]); // 太短
        Assert.Empty(sink.Calls);
    }
}
