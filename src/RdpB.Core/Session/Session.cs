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

    /// <summary>code review 複查時抓到的真坑:<see cref="EnterReconnectingOrClosed"/> 原本的
    /// 「只有 Active 才算第一次偵測到」守衛是 check-then-act,沒有鎖——HeartbeatLoopAsync
    /// (背景 Task)跟 OnDisconnected(transport 自己的讀取執行緒呼叫)真的可以在同一瞬間
    /// 都讀到 `_state == Active`,兩邊都通過守衛、都把 ReconnectCount 加一次、都開一個
    /// WaitForReconnectOrGiveUpAsync——前一版的修法只是把「循序呼叫會重複」縮小成「真的同時
    /// 呼叫才會重複」,競態窗口變窄了,但沒有真的關掉。這把鎖只包「讀狀態、決定要不要做、
    /// 真的把狀態寫掉」這一小段,不包往外丟事件/開新 Task 那幾行——避免在持有鎖的時候執行
    /// 外部的事件處理程式碼(StateChanged 的訂閱端),降低死鎖風險。</summary>
    private readonly object _enterReconnectingLock = new();

    public SessionState State => _state;
    public KeyStateTracker Keys => _keys;

    public event MessageReceivedHandler? MessageReceived;
    public event Action<SessionState>? StateChanged;

    /// <summary>計畫 §8 HUD「連線狀況」區塊要顯示的 RTT——每次量到新的值就觸發一次,
    /// 單位毫秒。量法見 <see cref="HeartbeatLoopAsync"/>/<see cref="OnMessageReceived"/>
    /// 上的註解:心跳本身當 ping/pong 用,不另外開一條專門量延遲的訊息。</summary>
    public event Action<double>? RttMeasured;

    public double? LastRttMs { get; private set; }

    /// <summary>code review 抓到的坑:HostListenerService 原本為了「定期重送 ConfigSync」
    /// 另外開了一個獨立的 <c>System.Threading.Timer</c>(5 秒一次),但 Session 自己早就有一個
    /// 固定週期跑的心跳迴圈(預設 500ms)在背景轉——兩套各自獨立的週期性機制做的是概念上同一件
    /// 事(定期告知對面一些狀態),各自有自己的生命週期要跟著 session/串流的 Dispose 對齊。
    /// 加這個事件讓呼叫端可以搭這班車,不用再另開一個 Timer——每次心跳迴圈真的送出 ping 時
    /// (只在 Active 狀態下)觸發一次,呼叫端自己用計數器決定多少個 tick 才做一次自己要做的事
    /// (見 HostListenerService 的用法:每 10 個 tick≈5 秒重送一次 ConfigSync)。</summary>
    public event Action? HeartbeatTick;

    /// <summary>計畫 §8 HUD「連線狀況」區塊要顯示的重連次數——每進入一次 Reconnecting 就算一次,
    /// 不管最後是接回來還是真的放棄。</summary>
    public int ReconnectCount { get; private set; }

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

            HandleHeartbeatPayload(payload);
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

                try { await _transport.SendAsync(Channel.Control, MessageType.Heartbeat, BuildPingPayload(), ct).ConfigureAwait(false); }
                catch { /* transport 自己的 Disconnected 事件會處理 */ }

                if (_state == SessionState.Active)
                    HeartbeatTick?.Invoke();

                if (_state == SessionState.Active &&
                    DateTime.UtcNow - _lastHeartbeatReceived > _heartbeatTimeout)
                {
                    EnterReconnectingOrClosed();
                }
            }
        }
        catch (OperationCanceledException) { /* 正常關閉 */ }
    }

    // Heartbeat 的 payload 格式:[1 byte flag][8 bytes DateTime.UtcNow.Ticks,little-endian]。
    // flag=0 是「ping,請原封不動送回來給我」,flag=1 是「這是你剛剛送的那個 ping 的回聲
    // (pong),我不會再送回去」——沒有這個區分,兩邊互相照原樣回送會變成無限彈回去的風暴。
    // 心跳本身的頻率(§1 預設 500ms)剛好也是計畫 §8 HUD 要的 RTT 更新頻率(連線狀況那塊
    // 建議 4Hz,這裡粗一點用心跳頻率,不另外開一條專門量延遲的訊息)。
    private const byte PingFlag = 0, PongFlag = 1;

    private static byte[] BuildPingPayload()
    {
        var buf = new byte[9];
        buf[0] = PingFlag;
        BitConverter.TryWriteBytes(buf.AsSpan(1), DateTime.UtcNow.Ticks);
        return buf;
    }

    private void HandleHeartbeatPayload(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 9) return; // 舊格式/對面還沒升級,沒有時戳就不量 RTT,不當錯誤

        byte flag = payload.Span[0];
        long ticks = BitConverter.ToInt64(payload.Span[1..9]);

        if (flag == PingFlag)
        {
            var pong = new byte[9];
            pong[0] = PongFlag;
            payload.Span[1..9].CopyTo(pong.AsSpan(1));
            _ = _transport.SendAsync(Channel.Control, MessageType.Heartbeat, pong);
        }
        else if (flag == PongFlag)
        {
            double rttMs = (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerMillisecond;
            if (rttMs < 0) return; // 時鐘跳動之類的離群值,不要回報負數嚇到使用者
            LastRttMs = rttMs;
            RttMeasured?.Invoke(rttMs);
        }
    }

    /// <summary>code review 抓到的真坑:同一次真正的斷線,心跳逾時那條路(HeartbeatLoopAsync)
    /// 和 transport 自己的 Disconnected 事件幾乎都會各觸發一次——<see cref="OnDisconnected"/>
    /// 原本只擋 <see cref="SessionState.Closed"/>,沒擋 <see cref="SessionState.Reconnecting"/>,
    /// 兩條路都呼叫到這裡就會把同一次斷線算兩次 ReconnectCount,還會多開一個重複的
    /// WaitForReconnectOrGiveUpAsync。只有「目前真的是 Active」才算是第一次偵測到這次斷線,
    /// 之後不管哪條路再呼叫進來都直接忽略——讓這個方法本身是 idempotent 的,不用在每個
    /// 呼叫端各自記一次「我有沒有處理過」。</summary>
    private void EnterReconnectingOrClosed()
    {
        lock (_enterReconnectingLock)
        {
            if (_state != SessionState.Active) return;
            ReconnectCount++;
            _state = SessionState.Reconnecting; // 直接寫 field,不經過 SetState——鎖裡只做這一步最小的事
        }

        // 贏得上面那把鎖的呼叫者才會走到這裡——保證只執行一次,不管是哪個執行緒先到。
        RequestKeyRelease(); // 計畫 §5:斷線/逾時立刻放開所有鍵,不等重連結果出來
        StateChanged?.Invoke(SessionState.Reconnecting);
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
