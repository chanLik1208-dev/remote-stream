using System.Collections.Concurrent;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;

namespace RdpB.Tests.TestSupport;

/// <summary>給 Session 單元測試用的假 transport——不碰網路,呼叫端自己決定何時「收到訊息」
/// 或「斷線」,專門測 Session 的心跳/重連/放開按鍵邏輯本身對不對。</summary>
internal sealed class FakeTransport : ITransport
{
    public bool IsConnected { get; private set; } = true;
    public event MessageReceivedHandler? MessageReceived;
    public event Action<Exception?>? Disconnected;

    public ConcurrentQueue<(Channel Channel, MessageType Type, byte[] Payload)> Sent { get; } = new();

    public Task SendAsync(Channel channel, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        Sent.Enqueue((channel, type, payload.ToArray()));
        return Task.CompletedTask;
    }

    public void SimulateReceive(Channel channel, MessageType type, byte[]? payload = null) =>
        MessageReceived?.Invoke(channel, type, payload ?? Array.Empty<byte>());

    public void SimulateDisconnect(Exception? ex = null)
    {
        IsConnected = false;
        Disconnected?.Invoke(ex);
    }

    public Task CloseAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
