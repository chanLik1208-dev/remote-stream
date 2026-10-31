using System.Security.Cryptography.X509Certificates;
using RdpB.Core.Protocol;
using RdpB.Core.Session;
using RdpB.Core.Transport;
using CoreSession = RdpB.Core.Session.Session;

namespace RdpB.Core.Pairing;

public enum PairingResult { Success, WrongPin, CertificateMismatch, Timeout, Disconnected }

/// <summary>
/// 計畫 §2 的配對流程,實際在網路上跑的那一半(<see cref="PairingService"/> 只有純計算)。
/// 兩邊角色不同:
///  - Client(<see cref="PairAsync"/>):連上後主動送 Pair 訊息,帶自己的憑證指紋 + HMAC tag,
///    等 Host 的 PairAck。
///  - Host(<see cref="WaitForPairingAsync"/>):收到 Pair 訊息,**一定要拿 TLS handshake 時
///    對方實際出示的憑證(<see cref="ITransportWithRemoteCertificate.RemoteCertificate"/>)去
///    驗證,不能只信 Pair 訊息裡自稱的指紋**——不然誰都可以謊報一個指紋混過驗證,§2 的配對
///    就形同虛設。驗證過了才回 PairAck(success)。
///
/// 兩邊都用同一個 <see cref="Core.Session.Session"/> 的 Control channel 送收,配對期間
/// Session 的心跳/斷線邏輯照常運作。
/// </summary>
public static class PairingCoordinator
{
    /// <summary>給使用者實際輸入 PIN 的時間,預設 2 分鐘——人要念螢幕上的數字、切過去打字,
    /// 30 秒在實測時太緊了(尤其這台機器的 CaptureEncoder.Start() 偶爾會因為 ddagrab 要等
    /// 畫面真的有變化才給出第一幀,啟動時間本身就有變動)。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>真的在這台機器上炸出來的坑:Host 端在真正訂閱 session.MessageReceived
    /// (WaitForPairingAsync 內部)之前,還要做幾件事(查信任清單讀磁碟、PIN 過期檢查、
    /// 搶並行配對名額的號誌)——不管這些檢查放在程式碼的哪個位置,只要跟「建立 session」
    /// 或「真的訂閱上」之間存在任何一個 await,Client 送的第一個 Pair 訊息理論上都可能
    /// 剛好搶在訂閱完成前就送達,直接憑空消失(C# 事件沒有訂閱者就是單純的 no-op,不會
    /// 緩衝)。寫一個會自己連續猜 PIN 的測試(完全沒有人類打字的等待時間),第 7、8 輪就
    /// 穩定踩到過一次,Host 端乾等整整 60 秒才放棄。
    ///
    /// 與其想辦法在 Host 端把這個縫隙徹底焊死(牽涉到要嘛完全重排檢查順序、要嘛在
    /// CoreSession 這一層加訊息緩衝,影響範圍大很多),改成 Client 端自己定期重送同一個
    /// Pair 訊息(tag 是一樣的,Host 收到的話重複的那幾份會因為 Host 端的 handler 第一件事
    /// 就是取消訂閱自己而被安靜忽略,不會重複處理)——這是這類「订阅之后才能收到」競態的
    /// 標準解法,網路協定裡到處都是(ARP/DHCP 這類也都是重送,不是想辦法消除競態窗口本身)。
    /// 對真人輸入 PIN 的正常流程完全無感,只是每隔這麼久送一次一模一樣的 bytes。</summary>
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(2);

    /// <summary>Client 端:主動發起配對。<paramref name="remoteCertificate"/> 是 TLS handshake
    /// 時真的連上的那個憑證(不是使用者自己輸入的),<paramref name="localCertificate"/> 是
    /// Client 自己的身分憑證。</summary>
    public static async Task<PairingResult> PairAsync(
        CoreSession session,
        string pin,
        X509Certificate2 remoteCertificate,
        X509Certificate2 localCertificate,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var hostFingerprint = PairingService.ComputeFingerprint(remoteCertificate.RawData);
        var clientFingerprint = PairingService.ComputeFingerprint(localCertificate.RawData);
        var tag = PairingService.ComputePairingTag(pin, hostFingerprint, clientFingerprint);

        var payload = PackPairMessage(clientFingerprint, tag);

        var tcs = new TaskCompletionSource<PairingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        MessageReceivedHandler? handler = null;
        handler = (channel, type, msgPayload) =>
        {
            if (channel != Channel.Control || type != MessageType.PairAck) return;
            session.MessageReceived -= handler;
            bool success = msgPayload.Length >= 1 && msgPayload.Span[0] == 1;
            tcs.TrySetResult(success ? PairingResult.Success : PairingResult.CertificateMismatch);
        };
        session.MessageReceived += handler;

        Action<SessionState>? stateHandler = null;
        stateHandler = state =>
        {
            if (state is SessionState.Closed)
            {
                session.StateChanged -= stateHandler;
                tcs.TrySetResult(PairingResult.Disconnected);
            }
        };
        session.StateChanged += stateHandler;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
            await using (timeoutCts.Token.Register(() => tcs.TrySetResult(PairingResult.Timeout)).ConfigureAwait(false))
            {
                // 見 ResendInterval 上的註解:第一次送完,每隔 ResendInterval 還沒收到回應
                // 就重送同一包——真人輸入 PIN 通常遠超過這個間隔,所以這個迴圈在正常使用時
                // 多半會重送個幾次都還在等人,完全不影響體感;會卡住 Host 端那個競態窗口
                // 通常只有幾毫秒,重送一次幾乎一定就接上了。
                while (!tcs.Task.IsCompleted)
                {
                    try
                    {
                        await session.SendAsync(Channel.Control, MessageType.Pair, payload, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; } // ct 被取消是呼叫端自己要的,不是斷線,往外丟讓呼叫端處理
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // code review 複查抓到的坑:原本只接 ObjectDisposedException,但重送
                        // 那一刻連線真的斷掉(對面關程式、網路斷線、TLS 連線被重置)丟出來的
                        // 通常是 SocketException/IOException,不是 ObjectDisposedException——
                        // 原本的程式碼會讓這些例外整個往外飄,砸穿 ClientConnectionService 的
                        // 「連線」按鈕,而不是回報成一個乾淨的 PairingResult。跟
                        // session.StateChanged 轉成 Closed 時的語意一樣,當斷線處理。
                        tcs.TrySetResult(PairingResult.Disconnected);
                        break;
                    }
                    await Task.WhenAny(tcs.Task, Task.Delay(ResendInterval, ct)).ConfigureAwait(false);
                }
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            session.MessageReceived -= handler;
            session.StateChanged -= stateHandler;
        }
    }

    /// <summary>Host 端:等對面送 Pair 訊息,驗證通過就回 PairAck(success),否則回
    /// PairAck(failure)。驗證用的是 TLS 實際出示的憑證,不是訊息裡自稱的指紋。</summary>
    public static async Task<(PairingResult Result, byte[]? ClientFingerprint)> WaitForPairingAsync(
        CoreSession session,
        string pin,
        X509Certificate2 remoteCertificate, // 這條連線 TLS handshake 時對面實際出示的憑證
        X509Certificate2 localCertificate,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<(PairingResult, byte[]?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualClientFingerprint = PairingService.ComputeFingerprint(remoteCertificate.RawData);
        var hostFingerprint = PairingService.ComputeFingerprint(localCertificate.RawData);

        MessageReceivedHandler? handler = null;
        handler = async (channel, type, payload) =>
        {
            if (channel != Channel.Control || type != MessageType.Pair) return;
            session.MessageReceived -= handler;

            if (!TryUnpackPairMessage(payload, out var claimedFingerprint, out var tag))
            {
                await SendAckAsync(session, success: false, ct).ConfigureAwait(false);
                tcs.TrySetResult((PairingResult.CertificateMismatch, null));
                return;
            }

            // 關鍵檢查:訊息裡自稱的指紋一定要跟 TLS 實際出示的憑證指紋一致,
            // 不然任何人都能謊報指紋混過配對。
            if (!claimedFingerprint.AsSpan().SequenceEqual(actualClientFingerprint))
            {
                await SendAckAsync(session, success: false, ct).ConfigureAwait(false);
                tcs.TrySetResult((PairingResult.CertificateMismatch, null));
                return;
            }

            bool valid = PairingService.VerifyPairingTag(pin, hostFingerprint, actualClientFingerprint, tag);
            await SendAckAsync(session, success: valid, ct).ConfigureAwait(false);
            tcs.TrySetResult(valid
                ? (PairingResult.Success, actualClientFingerprint)
                : (PairingResult.WrongPin, null));
        };
        session.MessageReceived += handler;

        Action<SessionState>? stateHandler = null;
        stateHandler = state =>
        {
            if (state is SessionState.Closed)
            {
                session.StateChanged -= stateHandler;
                tcs.TrySetResult((PairingResult.Disconnected, null));
            }
        };
        session.StateChanged += stateHandler;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
            await using (timeoutCts.Token.Register(() => tcs.TrySetResult((PairingResult.Timeout, null))).ConfigureAwait(false))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            session.MessageReceived -= handler;
            session.StateChanged -= stateHandler;
        }
    }

    private static Task SendAckAsync(CoreSession session, bool success, CancellationToken ct) =>
        session.SendAsync(Channel.Control, MessageType.PairAck, new byte[] { (byte)(success ? 1 : 0) }, ct);

    // Pair 訊息格式:[1 byte 指紋長度][指紋 bytes][1 byte tag 長度][tag bytes]。
    // 指紋/tag 都是 SHA-256/HMAC-SHA256 輸出,固定 32 bytes,但長度前綴讓格式不綁死這個假設。
    private static byte[] PackPairMessage(byte[] fingerprint, byte[] tag)
    {
        var buf = new byte[2 + fingerprint.Length + tag.Length];
        buf[0] = (byte)fingerprint.Length;
        fingerprint.CopyTo(buf, 1);
        buf[1 + fingerprint.Length] = (byte)tag.Length;
        tag.CopyTo(buf, 2 + fingerprint.Length);
        return buf;
    }

    private static bool TryUnpackPairMessage(ReadOnlyMemory<byte> payload, out byte[] fingerprint, out byte[] tag)
    {
        fingerprint = [];
        tag = [];
        var span = payload.Span;
        if (span.Length < 1) return false;

        int fpLen = span[0];
        if (span.Length < 1 + fpLen + 1) return false;
        fingerprint = span.Slice(1, fpLen).ToArray();

        int tagLen = span[1 + fpLen];
        if (span.Length < 2 + fpLen + tagLen) return false;
        tag = span.Slice(2 + fpLen, tagLen).ToArray();

        return true;
    }
}
