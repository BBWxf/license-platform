using System.Security.Cryptography;
using System.Text;
using LicensePlatform.Contracts.Leases;
using LicensePlatform.Infrastructure.Cryptography;
using Microsoft.Extensions.Options;

namespace LicensePlatform.IntegrationTests;

public sealed class Es256LeaseSigningTests
{
    [Fact]
    public void Sign_ProducesVerifiableP1363Es256CompactJws()
    {
        var options = Options.Create(new CryptographyOptions
        {
            DataProtectionKeyBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)0x55, 32).ToArray())
        });
        var protectedData = new AesGcmProtectedDataService(options);
        var keyFactory = new Es256ProductSigningKeyFactory(protectedData);
        var signer = new Es256LeaseSigningService(protectedData);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var productId = Guid.NewGuid();
        var key = keyFactory.Create(productId, now);
        var payload = new LeasePayloadDto(
            "license-platform",
            "desktop-client",
            productId,
            "EDITOR_PRO",
            Guid.NewGuid(),
            "device-hash",
            "installation-key-thumbprint",
            3,
            now,
            now,
            now.AddHours(6),
            now.AddDays(30),
            Guid.NewGuid().ToString("N"));

        var signed = signer.Sign(payload, key);
        var segments = signed.CompactToken.Split('.');

        Assert.Equal(3, segments.Length);
        Assert.Equal("ES256", signed.Algorithm);
        Assert.Equal(key.KeyId, signed.KeyId);

        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(key.PublicKeyPem);
        var signingInput = Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}");
        var signature = DecodeBase64Url(segments[2]);
        Assert.Equal(64, signature.Length);
        Assert.True(verifier.VerifyData(
            signingInput,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData(21601, 0)]
    [InlineData(0, 0)]
    [InlineData(-1, -60)]
    [InlineData(60, 1)]
    public void Sign_RejectsOverlongExpiredOrFutureNotBeforeLease(int expirationSeconds, int notBeforeSeconds)
    {
        var options = Options.Create(new CryptographyOptions
        {
            DataProtectionKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        });
        var protectedData = new AesGcmProtectedDataService(options);
        var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var productId = Guid.NewGuid();
        var key = new Es256ProductSigningKeyFactory(protectedData).Create(productId, now);
        var payload = new LeasePayloadDto(
            "license-platform", "TEST_APP", productId, "TEST_APP", Guid.NewGuid(),
            "device-hash", "installation-thumbprint", 1, now, now.AddSeconds(notBeforeSeconds),
            now.AddSeconds(expirationSeconds), now.AddDays(1), Guid.NewGuid().ToString("N"));

        Assert.Throws<LicensePlatform.Domain.Common.DomainException>(() =>
            new Es256LeaseSigningService(protectedData).Sign(payload, key));
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Convert.FromBase64String(base64);
    }
}
