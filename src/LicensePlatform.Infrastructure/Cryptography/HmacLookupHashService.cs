using System.Security.Cryptography;
using System.Text;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Domain.Licensing;
using Microsoft.Extensions.Options;

namespace LicensePlatform.Infrastructure.Cryptography;

public sealed class HmacLookupHashService : ILookupHashService
{
    private readonly byte[] _hmacKey;

    public HmacLookupHashService(IOptions<CryptographyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var encoded = options.Value.LookupHmacKeyBase64;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException($"{nameof(CryptographyOptions.LookupHmacKeyBase64)} is not configured.");
        }

        try
        {
            _hmacKey = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{nameof(CryptographyOptions.LookupHmacKeyBase64)} must be valid Base64.",
                exception);
        }

        if (_hmacKey.Length < 32)
        {
            CryptographicOperations.ZeroMemory(_hmacKey);
            throw new InvalidOperationException(
                $"{nameof(CryptographyOptions.LookupHmacKeyBase64)} must decode to at least 32 bytes.");
        }
    }

    public DeviceFingerprintHashes ComputeDeviceFingerprintHashes(
        string machineGuid,
        string smbiosUuid,
        string systemVolumeId)
    {
        return new DeviceFingerprintHashes(
            ComputeText("device:machine-guid", machineGuid, normalizeCase: true),
            ComputeText("device:smbios", smbiosUuid, normalizeCase: true),
            ComputeText("device:system-volume", systemVolumeId, normalizeCase: true));
    }

    public string ComputeDeviceId(DeviceFingerprintHashes fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var domain = Encoding.ASCII.GetBytes("device-id\0");
        var input = new byte[domain.Length + (DeviceFingerprintHashes.ComponentHashLength * 3)];
        domain.CopyTo(input, 0);
        fingerprint.MachineGuidHash.CopyTo(input, domain.Length);
        fingerprint.SmbiosHash.CopyTo(input, domain.Length + DeviceFingerprintHashes.ComponentHashLength);
        fingerprint.SystemVolumeHash.CopyTo(input, domain.Length + (DeviceFingerprintHashes.ComponentHashLength * 2));

        var hash = HMACSHA256.HashData(_hmacKey, input);
        try
        {
            return Base64Url.Encode(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domain);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public byte[] ComputeChallengeHash(string challengeToken) =>
        ComputeText("challenge", challengeToken, normalizeCase: false);

    public byte[] ComputeExportTokenHash(string exportToken) =>
        ComputeText("export", exportToken, normalizeCase: false);

    private byte[] ComputeText(string domain, string value, bool normalizeCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalizeCase)
        {
            normalized = normalized.ToUpperInvariant();
        }

        var bytes = Encoding.UTF8.GetBytes($"{domain}\0{normalized}");
        try
        {
            return HMACSHA256.HashData(_hmacKey, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
