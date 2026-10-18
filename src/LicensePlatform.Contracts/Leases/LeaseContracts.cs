namespace LicensePlatform.Contracts.Leases;

public sealed record LeasePayloadDto(
    string Issuer,
    string Audience,
    Guid ProductId,
    string ProductCode,
    Guid LicenseKeyId,
    string DeviceId,
    string InstallationKeyThumbprint,
    long BindingVersion,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? EntitlementExpiresAtUtc,
    string LeaseId);

public sealed record SignedLeaseDto(
    string CompactToken,
    string KeyId,
    string Algorithm,
    DateTimeOffset ExpiresAtUtc);
