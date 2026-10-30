using RdpB.Core.Pairing;

namespace RdpB.Tests;

public class PairingServiceTests
{
    [Fact]
    public void GeneratePin_IsSixDigits_WithLeadingZerosPreserved()
    {
        for (int i = 0; i < 50; i++)
        {
            var pin = PairingService.GeneratePin();
            Assert.Equal(6, pin.Length);
            Assert.True(int.TryParse(pin, out _));
        }
    }

    [Fact]
    public void VerifyPairingTag_Succeeds_WhenBothSidesUseSamePinAndFingerprints()
    {
        var pin = "482913";
        var hostFp = PairingService.ComputeFingerprint([1, 2, 3, 4]);
        var clientFp = PairingService.ComputeFingerprint([5, 6, 7, 8]);

        var tag = PairingService.ComputePairingTag(pin, hostFp, clientFp);

        Assert.True(PairingService.VerifyPairingTag(pin, hostFp, clientFp, tag));
    }

    [Fact]
    public void VerifyPairingTag_Fails_WhenPinWrong()
    {
        var hostFp = PairingService.ComputeFingerprint([1, 2, 3, 4]);
        var clientFp = PairingService.ComputeFingerprint([5, 6, 7, 8]);
        var tag = PairingService.ComputePairingTag("111111", hostFp, clientFp);

        Assert.False(PairingService.VerifyPairingTag("222222", hostFp, clientFp, tag));
    }

    [Fact]
    public void VerifyPairingTag_Fails_WhenCertificateSwapped_DetectsMitm()
    {
        var pin = "482913";
        var hostFp = PairingService.ComputeFingerprint([1, 2, 3, 4]);
        var clientFp = PairingService.ComputeFingerprint([5, 6, 7, 8]);
        var attackerFp = PairingService.ComputeFingerprint([9, 9, 9, 9]);

        var tag = PairingService.ComputePairingTag(pin, hostFp, clientFp);

        // 中間人換掉 host 憑證 → 指紋不同 → tag 驗證應該失敗
        Assert.False(PairingService.VerifyPairingTag(pin, attackerFp, clientFp, tag));
    }
}
