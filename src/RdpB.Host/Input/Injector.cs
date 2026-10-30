using RdpB.Core.Input;
using RdpB.Core.Protocol;

namespace RdpB.Host.Input;

/// <summary>
/// 把收到的輸入訊息解碼成實際的 SendInput 呼叫。Host 自己也維護一份
/// <see cref="KeyStateTracker"/>(計畫 §5 的雙保險):即使 Client 那邊的 RELEASE_ALL
/// 事件因為某種原因沒送到、或 Session 判定斷線,Host 自己知道按著什麼,可以自己放開。
/// </summary>
public sealed class Injector
{
    private readonly KeyStateTracker _keys = new();
    private readonly IInputSink _sink;
    public KeyStateTracker Keys => _keys;

    public Injector(IInputSink? sink = null) => _sink = sink ?? new RealInputSink();

    public void Handle(Channel channel, MessageType type, ReadOnlySpan<byte> payload)
    {
        switch (type)
        {
            case MessageType.Key when payload.Length >= 3:
            {
                ushort scanCode = (ushort)(payload[0] | (payload[1] << 8));
                bool down = (payload[2] & 1) != 0;
                bool extended = (payload[2] & 2) != 0;
                if (down) _keys.OnKeyDown(scanCode); else _keys.OnKeyUp(scanCode);
                _sink.SendKey(scanCode, down, extended);
                break;
            }
            case MessageType.MouseAbs when payload.Length >= 4:
            {
                ushort x = (ushort)(payload[0] | (payload[1] << 8));
                ushort y = (ushort)(payload[2] | (payload[3] << 8));
                _sink.MouseAbs(x, y);
                break;
            }
            case MessageType.MouseRel when payload.Length >= 4:
            {
                short dx = (short)(payload[0] | (payload[1] << 8));
                short dy = (short)(payload[2] | (payload[3] << 8));
                _sink.MouseRel(dx, dy);
                break;
            }
            case MessageType.MouseBtn when payload.Length >= 2:
            {
                byte button = payload[0];
                bool down = payload[1] != 0;
                if (down) _keys.OnMouseButtonDown(button); else _keys.OnMouseButtonUp(button);
                _sink.MouseBtn(button, down);
                break;
            }
            case MessageType.Wheel when payload.Length >= 3:
            {
                short delta = (short)(payload[0] | (payload[1] << 8));
                bool horizontal = payload[2] != 0;
                _sink.Wheel(delta, horizontal);
                break;
            }
        }
    }

    /// <summary>計畫 §5 的雙保險:把目前還按著的鍵全部放開。Session 的 KeyReleaseRequested
    /// 事件、或心跳逾時/斷線時都要呼叫這個,不等 Client 那邊的訊息。</summary>
    public void ReleaseAll()
    {
        var snapshot = _keys.ReleaseAll();
        foreach (var scanCode in snapshot.Keys)
            _sink.SendKey(scanCode, down: false, extended: false);
        foreach (var button in snapshot.MouseButtons)
            _sink.MouseBtn(button, down: false);
    }
}
