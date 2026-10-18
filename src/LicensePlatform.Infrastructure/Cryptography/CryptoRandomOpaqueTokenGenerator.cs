using System.Security.Cryptography;
using LicensePlatform.Application.Abstractions;

namespace LicensePlatform.Infrastructure.Cryptography;

public sealed class CryptoRandomOpaqueTokenGenerator : IOpaqueTokenGenerator
{
    public string Generate(int entropyBytes = 32)
    {
        if (entropyBytes is < 16 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(entropyBytes), "Token entropy must be between 16 and 256 bytes.");
        }

        var entropy = RandomNumberGenerator.GetBytes(entropyBytes);
        try
        {
            return Base64Url.Encode(entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }
}
