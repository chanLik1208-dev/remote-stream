using RdpB.Core.Protocol;

namespace RdpB.Core.Transport;

public delegate void MessageReceivedHandler(Channel channel, MessageType type, ReadOnlyMemory<byte> payload);

/// <summary>
/// 計畫 §1:同時提供可靠有序訊息(Control/Input)和可丟棄的媒體(Audio/Video)。
/// QUIC、TCP+TLS、WebSocket+TLS 都實作這個介面,上層(Session)不用管底下走哪一種。
/// </summary>
public interface ITransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>收到任何 channel 的訊息時觸發。在背景讀取執行緒上呼叫,訂閱端自己決定要不要排程回主執行緒。</summary>
    event MessageReceivedHandler? MessageReceived;

    /// <summary>連線異常中斷時觸發一次(正常呼叫 CloseAsync 不會觸發)。</summary>
    event Action<Exception?>? Disconnected;

    Task SendAsync(Channel channel, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    Task CloseAsync();
}
