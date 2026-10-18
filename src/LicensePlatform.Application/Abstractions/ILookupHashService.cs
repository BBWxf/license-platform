using LicensePlatform.Domain.Licensing;

namespace LicensePlatform.Application.Abstractions;

public interface ILookupHashService
{
    DeviceFingerprintHashes ComputeDeviceFingerprintHashes(
        string machineGuid,
        string smbiosUuid,
        string systemVolumeId);

    string ComputeDeviceId(DeviceFingerprintHashes fingerprint);

    byte[] ComputeChallengeHash(string challengeToken);

    byte[] ComputeExportTokenHash(string exportToken);
}
