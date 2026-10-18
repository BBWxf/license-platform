using System.Security.Cryptography;
using LicensePlatform.Infrastructure.Cryptography;

namespace LicensePlatform.IntegrationTests;

public sealed class DeviceProofVerifierTests
{
    [Fact]
    public void VerifyEs256_AcceptsValidProofAndRejectsChangedNonce()
    {
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var signature = deviceKey.SignData(
            nonce,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var encodedSignature = Convert.ToBase64String(signature)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var verifier = new Es256DeviceProofVerifier();

        Assert.True(verifier.VerifyEs256(deviceKey.ExportSubjectPublicKeyInfoPem(), nonce, encodedSignature));

        nonce[0] ^= 0x01;
        Assert.False(verifier.VerifyEs256(deviceKey.ExportSubjectPublicKeyInfoPem(), nonce, encodedSignature));
    }

    [Fact]
    public void VerifyEs256_RejectsNonP256AndPrivateKeyPem()
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var p384Signature = Base64Url(p384.SignData(
            nonce,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p256Signature = Base64Url(p256.SignData(
            nonce,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var verifier = new Es256DeviceProofVerifier();

        Assert.False(verifier.VerifyEs256(p384.ExportSubjectPublicKeyInfoPem(), nonce, p384Signature));
        Assert.False(verifier.VerifyEs256(p256.ExportPkcs8PrivateKeyPem(), nonce, p256Signature));
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
