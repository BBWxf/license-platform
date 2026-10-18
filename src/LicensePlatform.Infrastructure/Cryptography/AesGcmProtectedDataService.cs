using System.Security.Cryptography;
using System.Text;
using LicensePlatform.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace LicensePlatform.Infrastructure.Cryptography;

public sealed class AesGcmProtectedDataService : IProtectedDataService
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    private readonly byte[] _rootKey;

    public AesGcmProtectedDataService(IOptions<CryptographyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var encoded = options.Value.DataProtectionKeyBase64;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException($"{nameof(CryptographyOptions.DataProtectionKeyBase64)} is not configured.");
        }

        try
        {
            _rootKey = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{nameof(CryptographyOptions.DataProtectionKeyBase64)} must be valid Base64.",
                exception);
        }

        if (_rootKey.Length < 32)
        {
            CryptographicOperations.ZeroMemory(_rootKey);
            throw new InvalidOperationException(
                $"{nameof(CryptographyOptions.DataProtectionKeyBase64)} must decode to at least 32 bytes.");
        }
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        ValidatePurpose(purpose);
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        var key = HMACSHA256.HashData(_rootKey, purposeBytes);
        var result = new byte[checked(HeaderSize + plaintext.Length)];
        result[0] = FormatVersion;
        var nonce = result.AsSpan(1, NonceSize);
        var tag = result.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = result.AsSpan(HeaderSize);
        RandomNumberGenerator.Fill(nonce);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, purposeBytes);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(purposeBytes);
        }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData, string purpose)
    {
        ValidatePurpose(purpose);
        if (protectedData.Length < HeaderSize || protectedData[0] != FormatVersion)
        {
            throw new CryptographicException("The protected payload format is invalid or unsupported.");
        }

        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        var key = HMACSHA256.HashData(_rootKey, purposeBytes);
        var plaintext = new byte[protectedData.Length - HeaderSize];
        var nonce = protectedData.Slice(1, NonceSize);
        var tag = protectedData.Slice(1 + NonceSize, TagSize);
        var ciphertext = protectedData[HeaderSize..];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, purposeBytes);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(purposeBytes);
        }
    }

    private static void ValidatePurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (purpose.Length > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), "Protection purpose cannot exceed 500 characters.");
        }
    }
}
