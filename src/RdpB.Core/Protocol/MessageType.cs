namespace RdpB.Core.Protocol;

/// <summary>訊息類型。沿用 remote-stream 的協定精神(簡單二進位 tag + payload),
/// 但這次每種訊息都知道自己該走哪個 Channel,而且是可靠多工 Session 之上的訊息,不是裸 UDP。</summary>
public enum MessageType : byte
{
    // ── Control channel ──
    Hello = 0,
    Pair = 1,           // 配對請求(PIN)
    PairAck = 2,
    Heartbeat = 3,
    ReleaseAll = 4,     // 強制放開所有按鍵(焦點丟失/斷線/切換控制)
    ConfigSync = 5,
    Bye = 6,

    // ── Input channel ──
    Key = 10,
    MouseAbs = 11,
    MouseRel = 12,
    MouseBtn = 13,
    Wheel = 14,
    Pad = 15,

    // ── Audio channel ──
    AudioFrame = 20,

    // ── Video channel ──
    VideoFrame = 30,
    VideoKeyframeRequest = 31, // Client 跟 Host 要求立刻出一張 IDR(例如剛丟過幀)
}
