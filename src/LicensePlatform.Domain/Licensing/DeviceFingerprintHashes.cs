using System.Security.Cryptography;
using LicensePlatform.Domain.Common;

namespace LicensePlatform.Domain.Licensing;

public sealed class DeviceFingerprintHashes
{
    public const int ComponentHashLength = 32;

    public DeviceFingerprintHashes(
        byte[] machineGuidHash,
        byte[] smbiosHash,
        byte[] systemVolumeHash)
    {
        MachineGuidHash = Guard.Bytes(machineGuidHash, nameof(machineGuidHash), ComponentHashLength);
        SmbiosHash = Guard.Bytes(smbiosHash, nameof(smbiosHash), ComponentHashLength);
        SystemVolumeHash = Guard.Bytes(systemVolumeHash, nameof(systemVolumeHash), ComponentHashLength);
    }

    public byte[] MachineGuidHash { get; }

    public byte[] SmbiosHash { get; }

    public byte[] SystemVolumeHash { get; }

    public int CountMatches(DeviceFingerprintHashes candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var matches = 0;
        matches += FixedEquals(MachineGuidHash, candidate.MachineGuidHash) ? 1 : 0;
        matches += FixedEquals(SmbiosHash, candidate.SmbiosHash) ? 1 : 0;
        matches += FixedEquals(SystemVolumeHash, candidate.SystemVolumeHash) ? 1 : 0;
        return matches;
    }

    public bool MatchesAtLeast(DeviceFingerprintHashes candidate, int requiredMatches = 2)
    {
        if (requiredMatches is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredMatches));
        }

        return CountMatches(candidate) >= requiredMatches;
    }

    private static bool FixedEquals(byte[] left, byte[] right)
    {
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
