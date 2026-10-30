namespace RdpB.Core.Media;

public enum AudioFrameAction
{
    PlayNormally,       // 依序到達,正常解碼播放
    ConcealThenPlay,     // 中間掉了幾幀,先用 Opus PLC 補幾幀再播放這一幀
    Drop,                // 重複或是比已經播過的還舊的封包(亂序到達),整包丟掉
}

public readonly record struct AudioFrameDecision(AudioFrameAction Action, int ConcealFrameCount);

/// <summary>
/// 計畫 §9「序號 + Opus PLC(掉包補償)」的判斷邏輯——跟 NAudio/Concentus 完全分開,
/// 方便寫測試。序號是 16-bit 會繞回去,所有比較都要用繞回安全的算法(unchecked ushort 減法)。
/// </summary>
public sealed class AudioSequenceTracker
{
    /// <summary>連續遺失超過這個幀數就不補了,直接接上新的——20ms 一幀,10 幀是 200ms,
    /// 再補下去播出來的音訊會聽起來比真的斷線還怪。</summary>
    public const int MaxConcealFrames = 10;

    /// <summary>序號比期望值「倒退」超過這個量,視為舊封包亂序到達或重複,直接丟棄不處理。</summary>
    private const int OutOfOrderThreshold = 1000;

    private ushort? _lastSeq;

    public AudioFrameDecision Observe(ushort seq)
    {
        if (_lastSeq is null)
        {
            _lastSeq = seq;
            return new AudioFrameDecision(AudioFrameAction.PlayNormally, 0);
        }

        ushort expected = (ushort)(_lastSeq.Value + 1);
        int diff = (ushort)(seq - expected); // 0..65535,繞回安全

        if (diff == 0)
        {
            _lastSeq = seq;
            return new AudioFrameDecision(AudioFrameAction.PlayNormally, 0);
        }

        if (diff >= 65536 - OutOfOrderThreshold)
        {
            // seq 比期望值還「舊」——亂序到達的舊封包或重複封包,不更新 _lastSeq,直接丟掉。
            return new AudioFrameDecision(AudioFrameAction.Drop, 0);
        }

        if (diff <= MaxConcealFrames)
        {
            _lastSeq = seq;
            return new AudioFrameDecision(AudioFrameAction.ConcealThenPlay, diff);
        }

        // 掉太多幀,補下去沒意義,直接接上新的(等於重新同步,不補償)。
        _lastSeq = seq;
        return new AudioFrameDecision(AudioFrameAction.PlayNormally, 0);
    }

    public void Reset() => _lastSeq = null;
}
