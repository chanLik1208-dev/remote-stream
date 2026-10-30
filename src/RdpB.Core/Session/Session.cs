using RdpB.Core.Input;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;

namespace RdpB.Core.Session;

public enum SessionState { Connecting, Active, Reconnecting, Closed }

/// <summary>
/// 包住一個 <see cref="ITransport"/>,補上計畫 §1 的心跳/斷線/重連邏輯,以及 §5
/// 「雙保險放開按鍵」:心跳逾時或 transport 斷線時,立刻呼叫 <see cref="KeyStateTracker.ReleaseAll"/>
/// 並把放開事件送給呼叫端去真正注入/擷取端處理(Host 端會拿去 SendInput,Client 端拿去確保本機
/// 鍵鼠已經不再被攔截)。
///
/// 這一層不知道自己在 Host 還是 Client——兩邊都一樣需要「斷線就放開按鍵」,差別只在
/// <see cref="KeyReleaseRequested"/> 事件之後做什麼。
/// </summary>
public sealed class Session : IAsyncDisposable
{
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly TimeSpan _reconnectWindow;

    private readonly KeyStateTracker _keys = new();
    private readonly CancellationTokenSource _cts = new();
    private ITransport _transport;
    private DateTime _lastHeartbeatReceived = DateTime.UtcNow;
    private Task? _heartbeatLoop;
    private volatile SessionState _state = SessionState.Connecting;

    public SessionState State => _state;
    public KeyStateTracker Keys => _keys;

    public event MessageReceivedHandler? MessageReceived;
    public event Action<SessionState>? StateChanged;

    /// <summary>進到 Reconnecting 或 Closed 時觸發,附上這次 ReleaseAll 的快照——
    /// 呼叫端(Host/Client 的輸入模組)拿這份快照去做實際的放開動作。</summary>
    public event Action<KeyStateTracker.ReleaseSnapshot>? KeyReleaseRequested;

    /// <summary>計畫 §1 的預設值:心跳 500ms 一次,2 秒逾時判斷斷線,重連等 30 秒。
    /// 測試可以傳短一點的值,不用真的等 30 秒。</summary>
    public Session(ITransport transport,
        TimeSpan? heartbeatInterval = null, TimeSpan? heartbeatTimeout = null, TimeSpan? reconnectWindow = null)
    {
        _transport = transport;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromMilliseconds(500);
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(2);
        _reconnectWindow = reconnectWindow ?? TimeSpan.FromSeconds(30);
        Wire(transport);
    }

    private void Wire(ITransport transport)
    {
        transport.MessageReceived += OnMessageReceived;
        transport.Disconnected += OnDisconnected;
    }

    private void Unwire(ITransport transport)
    {
        transport.MessageReceived -= OnMessageReceived;
        transport.Disconnected -= OnDisconnected;
    }

    public void Start()
    {
        SetState(SessionState.Active);
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    private void OnMessageReceived(Channel channel, MessageType type, ReadOnlyMemory<byte> payload)
    {
        if (type == MessageType.Heartbeat)
        {
            _lastHeartbeatReceived = DateTime.UtcNow;
            if (_state == SessionState.Reconnecting)
                SetState(SessionState.Active);
            return;
        }

        if (type == MessageType.ReleaseAll)
        {
            RequestKeyRelease();
            return;
        }

        MessageReceived?.Invoke(channel, type, payload);
    }

    private void OnDisconnected(Exception? ex)
    {
        if (_state == SessionState.Closed) return;
        EnterReconnectingOrClosed();
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, ct).ConfigureAwait(false);

                try { await _transport.SendAsync(Channel.Control, MessageType.Heartbeat, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false); }
                catch { /* transport 自己的 Disconnected 事件會處理 */ }

                if (_state == SessionState.Active &&
                    DateTime.UtcNow - _lastHeartbeatReceived > _heartbeatTimeout)
                {
                    EnterReconnectingOrClosed();
                }
            }
        }
        catch (OperationCanceledException) { /* 正常關閉 */ }
    }

    private void EnterReconnectingOrClosed()
    {
        RequestKeyRelease(); // 計畫 §5:斷線/逾時立刻放開所有鍵,不等重連結果出來
        SetState(SessionState.Reconnecting);
        _ = WaitForReconnectOrGiveUpAsync();
    }

    private async Task WaitForReconnectOrGiveUpAsync()
    {
        var deadline = DateTime.UtcNow + _reconnectWindow;
        while (_state == SessionState.Reconnecting && DateTime.UtcNow < deadline)
            await Task.Delay(200).ConfigureAwait(false);

        if (_state == SessionState.Reconnecting)
            SetState(SessionState.Closed);
    }

    /// <summary>重連成功後,呼叫端(Host 的 listener 迴圈 / Client 的連線邏輯)用新的 transport
    /// 替換掉舊的,session 狀態繼續沿用(不用重新配對)。</summary>
    public void Rebind(ITransport newTransport)
    {
        Unwire(_transport);
        _transport = newTransport;
        Wire(newTransport);
        _lastHeartbeatReceived = DateTime.UtcNow;
        SetState(SessionState.Active);
    }

    private void RequestKeyRelease()
    {
        var snapshot = _keys.ReleaseAll();
        if (!snapshot.IsEmpty)
            KeyReleaseRequested?.Invoke(snapshot);
    }

    private void SetState(SessionState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public Task SendAsync(Channel channel, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
        _transport.SendAsync(channel, type, payload, ct);

    public async ValueTask DisposeAsync()
    {
        SetState(SessionState.Closed);
        _cts.Cancel();
        RequestKeyRelease();
        if (_heartbeatLoop is not null)
        {
            try { await _heartbeatLoop.ConfigureAwait(false); } catch { }
        }
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
