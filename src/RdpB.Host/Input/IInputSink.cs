namespace RdpB.Host.Input;

/// <summary>Injector 實際注入動作的出口,抽成介面是為了能在單元測試裡換成假的,
/// 不用在 CI 裡真的呼叫 SendInput(那需要互動桌面工作階段,見 ROADMAP 已知限制)。</summary>
public interface IInputSink
{
    void SendKey(ushort scanCode, bool down, bool extended);
    void MouseAbs(ushort x, ushort y);
    void MouseRel(short dx, short dy);
    void MouseBtn(byte button, bool down);
    void Wheel(short delta, bool horizontal);
}

/// <summary>正式環境用的:直接呼叫 <see cref="Native"/> 的 SendInput 包裝。</summary>
public sealed class RealInputSink : IInputSink
{
    public void SendKey(ushort scanCode, bool down, bool extended) => Native.SendKey(scanCode, down, extended);
    public void MouseAbs(ushort x, ushort y) => Native.MouseAbs(x, y);
    public void MouseRel(short dx, short dy) => Native.MouseRel(dx, dy);
    public void MouseBtn(byte button, bool down) => Native.MouseBtn(button, down);
    public void Wheel(short delta, bool horizontal) => Native.Wheel(delta, horizontal);
}
