using RdpB.Core.Protocol;

namespace RdpB.Core.Transport;

/// <summary>
/// 在一條可靠、有序的 <see cref="Stream"/> 上跑 Frame 協定的讀寫迴圈:
/// 寫入端從 <see cref="ChannelSendQueue"/> 依優先序取 frame 送出,讀取端持續解析進來的 frame。
///
/// 這段邏輯在 TCP+TLS transport 的「整條連線」和 QUIC transport 的「可靠 bidi stream」上
/// 是完全一樣的——兩者都是一條有序的 byte stream,差別只在外層怎麼建立連線——所以抽出來
/// 共用,不要兩邊各寫一份會走鐘的版本。
/// </summary>
public sealed class FrameStreamPump
{
    private readonly Stream _stream;
    private readonly ChannelSendQueue _sendQueue;
    private Task? _readLoop;
    private Task? _writeLoop;

    public event MessageReceivedHandler? MessageReceived;
    public event Action<Exception>? Faulted;

    public FrameStreamPump(Stream stream, ChannelSendQueue sendQueue)
    {
        _stream = stream;
        _sendQueue = sendQueue;
    }

    public void Start(CancellationToken ct)
    {
        _readLoop = Task.Run(() => ReadLoopAsync(ct));
        _writeLoop = Task.Run(() => WriteLoopAsync(ct));
    }

    public async Task StopAsync()
    {
        if (_readLoop is not null) await SafeWaitAsync(_readLoop).ConfigureAwait(false);
        if (_writeLoop is not null) await SafeWaitAsync(_writeLoop).ConfigureAwait(false);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            var headerBuf = new byte[Frame.HeaderSize];
            while (!ct.IsCancellationRequested)
            {
                var frame = await _sendQueue.DequeueAsync(ct).ConfigureAwait(false);
                frame.WriteHeaderTo(headerBuf);
                await _stream.WriteAsync(headerBuf, ct).ConfigureAwait(false);
                if (frame.Payload.Length > 0)
                    await _stream.WriteAsync(frame.Payload, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常關閉 */ }
        catch (Exception ex) { Faulted?.Invoke(ex); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            var headerBuf = new byte[Frame.HeaderSize];
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(headerBuf, ct).ConfigureAwait(false);

                var channel = (Channel)headerBuf[0];
                var type = (MessageType)headerBuf[1];
                int len = BitConverter.ToInt32(headerBuf, 2);
                if (len < 0 || len > 64 * 1024 * 1024)
                    throw new InvalidDataException($"收到不合理的 frame 長度:{len}");

                var payload = len == 0 ? Array.Empty<byte>() : new byte[len];
                if (len > 0)
                    await ReadExactAsync(payload, ct).ConfigureAwait(false);

                MessageReceived?.Invoke(channel, type, payload);
            }
        }
        catch (OperationCanceledException) { /* 正常關閉 */ }
        catch (Exception ex) { Faulted?.Invoke(ex); }
    }

    private async Task ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(offset, buf.Length - offset), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("連線被對面關閉");
            offset += n;
        }
    }

    private static async Task SafeWaitAsync(Task t)
    {
        try { await t.ConfigureAwait(false); } catch { /* 已經處理過的例外 */ }
    }
}
