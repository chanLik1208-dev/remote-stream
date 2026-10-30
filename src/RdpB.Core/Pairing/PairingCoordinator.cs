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
            await session.SendAsync(Channel.Control, MessageType.Pair, payload, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
            await using (timeoutCts.Token.Register(() => tcs.TrySetResult(PairingResult.Timeout)).ConfigureAwait(false))
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
