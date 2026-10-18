using System.Security.Cryptography;
using System.Text;
using LicensePlatform.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace LicensePlatform.Infrastructure.Cryptography;

public sealed class HmacLicenseKeyCryptography : ILicenseKeyCryptography
{
    public const int EntropyBytes = 20;
    public const int NormalizedLength = 32;

    private readonly byte[] _hmacKey;

    public HmacLicenseKeyCryptography(IOptions<CryptographyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hmacKey = DecodeKey(options.Value.LicenseKeyHmacKeyBase64, nameof(CryptographyOptions.LicenseKeyHmacKeyBase64));
    }

    public GeneratedLicenseKey Generate(string licensePrefix)
    {
        var prefix = NormalizePrefix(licensePrefix);
        Span<byte> entropy = stackalloc byte[EntropyBytes];
        RandomNumberGenerator.Fill(entropy);
        var body = CrockfordBase32.Encode(entropy);
        if (body.Length != NormalizedLength)
        {
            throw new CryptographicException("The generated license key has an unexpected length.");
        }

        var groupedBody = string.Join('-', Enumerable.Range(0, 8).Select(index => body.Substring(index * 4, 4)));
        var normalized = $"{prefix}-{body}";
        var formatted = $"{prefix}-{groupedBody}";
        var hash = ComputeNormalizedHash(normalized);
        var hint = $"{prefix}-{body[..4]}…{body[^4..]}";
        CryptographicOperations.ZeroMemory(entropy);
        return new GeneratedLicenseKey(prefix, formatted, normalized, hash, hint);
    }

    public string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        var separatorIndex = trimmed.IndexOf('-');
        if (separatorIndex <= 0 || separatorIndex == trimmed.Length - 1)
        {
            throw new FormatException("A license key must contain a product prefix followed by '-'.");
        }

        var prefix = NormalizePrefix(trimmed[..separatorIndex]);
        var body = CrockfordBase32.Normalize(trimmed[(separatorIndex + 1)..]);
        if (body.Length != NormalizedLength)
        {
            throw new FormatException($"A license key must contain exactly {NormalizedLength} Crockford Base32 entropy characters.");
        }

        return $"{prefix}-{body}";
    }

    public string GetPrefix(string value)
    {
        var normalized = Normalize(value);
        return normalized[..normalized.IndexOf('-')];
    }

    public byte[] ComputeLookupHash(string value)
    {
        return ComputeNormalizedHash(Normalize(value));
    }

    private byte[] ComputeNormalizedHash(string normalized)
    {
        var valueBytes = Encoding.ASCII.GetBytes(normalized);
        try
        {
            return HMACSHA256.HashData(_hmacKey, valueBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(valueBytes);
        }
    }

    private static string NormalizePrefix(string licensePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licensePrefix);
        var normalized = licensePrefix.Trim().ToUpperInvariant();
        if (normalized.Length is < 2 or > 12
            || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new FormatException("License prefix must contain 2 to 12 ASCII letters or digits.");
        }

        return normalized;
    }

    private static byte[] DecodeKey(string encoded, string name)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException($"{name} is not configured.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"{name} must be valid Base64.", exception);
        }

        if (key.Length < 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException($"{name} must decode to at least 32 bytes.");
        }

        return key;
    }
}
