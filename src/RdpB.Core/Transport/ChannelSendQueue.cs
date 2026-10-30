using System.Collections.Concurrent;
using RdpB.Core.Protocol;

namespace RdpB.Core.Transport;

/// <summary>
/// TCP/WS transport 用的多工送出佇列(計畫 §1「防延遲累積」)。
///
/// 每個 Channel 有自己的佇列。取下一筆要送的 frame 時,永遠先看數字較小(優先序較高)
/// 的 channel——Control/Input 優先於 Audio,Audio 優先於 Video。可丟棄的 channel
/// (<see cref="ChannelPolicy.IsDroppable"/>)一旦深度超過門檻,新進的 frame 會把
/// 佇列裡最舊的一筆直接丟掉再塞進去,避免延遲隨著網路壅塞無限累積——這正是 QUIC
/// transport 不需要處理、但單一 TCP 連線上必須自己做的事。
/// </summary>
public sealed class ChannelSendQueue
{
    private readonly ConcurrentDictionary<Channel, ConcurrentQueue<Frame>> _queues = new();
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>佇列裡有 frame 因為超過深度門檻被丟棄時觸發,payload 是(channel, 丟棄前的佇列深度)。
    /// Video channel 丟幀後,呼叫端應該據此通知編碼器出一張 IDR。</summary>
    public event Action<Channel, int>? FrameDropped;

    public void Enqueue(Frame frame)
    {
        var q = _queues.GetOrAdd(frame.Channel, _ => new ConcurrentQueue<Frame>());
        q.Enqueue(frame);

        if (ChannelPolicy.IsDroppable(frame.Channel))
        {
            int limit = ChannelPolicy.QueueDepth(frame.Channel);
            while (q.Count > limit && q.TryDequeue(out _))
            {
                FrameDropped?.Invoke(frame.Channel, q.Count + 1);
            }
        }

        _signal.Release();
    }

    /// <summary>依優先序(Channel 的數字越小越優先)取出下一筆要送的 frame;佇列全空就等待。</summary>
    public async Task<Frame> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            if (TryDequeueHighestPriority(out var frame))
                return frame;

            await _signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public bool TryDequeueHighestPriority(out Frame frame)
    {
        foreach (var channel in _queues.Keys.OrderBy(c => (byte)c))
        {
            if (_queues[channel].TryDequeue(out frame))
                return true;
        }
        frame = default;
        return false;
    }

    public int CountInChannel(Channel channel) =>
        _queues.TryGetValue(channel, out var q) ? q.Count : 0;
}
