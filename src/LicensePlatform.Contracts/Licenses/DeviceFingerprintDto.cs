namespace LicensePlatform.Contracts.Licenses;

public sealed record DeviceFingerprintDto(
    string MachineGuid,
    string SmbiosUuid,
    string SystemVolumeId);
