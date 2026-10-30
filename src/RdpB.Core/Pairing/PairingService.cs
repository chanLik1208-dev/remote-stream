using System.Security.Cryptography;
using System.Text;

namespace RdpB.Core.Pairing;

/// <summary>
/// 計畫 §2 的配對邏輯:Host 顯示 6 位數 PIN,雙方用 PIN 做 HMAC 驗證對方憑證指紋,
/// 之後 Client 釘選(pin)這個指紋,連線就是 mTLS + 指紋比對,不用再輸入 PIN。
/// 這個類別只管「用 PIN 和憑證指紋算出/驗證 HMAC」,不碰 TLS 本身——
/// TLS 的 RemoteCertificateValidationCallback 呼叫這裡驗證指紋即可。
/// </summary>
public static class PairingService
{
    /// <summary>產生 6 位數字 PIN(字串保留前導零),有效期由呼叫端自己控管(計畫 UI 是 5 分鐘)。</summary>
    public static string GeneratePin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>憑證的 SHA-256 指紋(DER 編碼的 raw bytes 雜湊),用來之後釘選比對,不比對 Subject/有效期等會變動的欄位。</summary>
    public static byte[] ComputeFingerprint(byte[] certificateRawData) =>
        SHA256.HashData(certificateRawData);

    /// <summary>
    /// 用雙方各自的憑證指紋 + PIN 算一個 HMAC,雙方各自算出來比對是否一致即完成配對。
    /// 把 PIN 當 key、兩份指紋串起來當訊息,這樣只有知道 PIN 的一方才能算出正確的值,
    /// 而且兩份指紋都放進去,中間人換掉任何一邊的憑證都會讓雙方算出的值不一致。
    /// </summary>
    public static byte[] ComputePairingTag(string pin, byte[] hostFingerprint, byte[] clientFingerprint)
    {
        var key = Encoding.UTF8.GetBytes(pin);
        using var hmac = new HMACSHA256(key);
        var message = new byte[hostFingerprint.Length + clientFingerprint.Length];
        hostFingerprint.CopyTo(message, 0);
        clientFingerprint.CopyTo(message, hostFingerprint.Length);
        return hmac.ComputeHash(message);
    }

    public static bool VerifyPairingTag(string pin, byte[] hostFingerprint, byte[] clientFingerprint, byte[] expectedTag)
    {
        var actual = ComputePairingTag(pin, hostFingerprint, clientFingerprint);
        return CryptographicOperations.FixedTimeEquals(actual, expectedTag);
    }
}

/// <summary>配對完成後,Client 端記住的「這台 Host 可信」記錄。存進 config.json 的那種資料,見 §8。</summary>
public sealed record PinnedHost(string Name, string HostAddress, byte[] CertificateFingerprint);
