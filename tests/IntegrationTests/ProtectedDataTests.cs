using System.Security.Cryptography;
using System.Text;
using LicensePlatform.Infrastructure.Cryptography;
using Microsoft.Extensions.Options;

namespace LicensePlatform.IntegrationTests;

public sealed class ProtectedDataTests
{
    private readonly AesGcmProtectedDataService _service = new(Options.Create(new CryptographyOptions
    {
        DataProtectionKeyBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)0x77, 32).ToArray())
    }));

    [Fact]
    public void ProtectAndUnprotect_RoundTripsForSamePurpose()
    {
        var plaintext = Encoding.UTF8.GetBytes("sensitive export payload");

        var protectedData = _service.Protect(plaintext, "batch-export:1");
        var restored = _service.Unprotect(protectedData, "batch-export:1");

        Assert.Equal(plaintext, restored);
        Assert.NotEqual(plaintext, protectedData);
    }

    [Fact]
    public void Unprotect_RejectsDifferentPurposeAndTampering()
    {
        var protectedData = _service.Protect([1, 2, 3, 4], "purpose-a");

        Assert.ThrowsAny<CryptographicException>(() => _service.Unprotect(protectedData, "purpose-b"));

        protectedData[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => _service.Unprotect(protectedData, "purpose-a"));
    }
}
