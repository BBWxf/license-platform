using LicensePlatform.Contracts.Common;

namespace LicensePlatform.Contracts.Licenses;

public sealed record DeviceBindingDto(
    Guid Id,
    string DeviceHint,
    long BindingVersion,
    DateTimeOffset BoundAtUtc,
    DateTimeOffset? UnboundAtUtc);

public sealed record LicenseKeyDto(
    Guid Id,
    Guid ProductId,
    Guid BatchId,
    string KeyHint,
    LicenseTermDto Term,
    int AdditionalDays,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? DisabledReason,
    long BindingVersion,
    DeviceBindingDto? ActiveBinding,
    long Version);

public sealed record DisableLicenseRequest(string Reason, long ExpectedVersion);

public sealed record ExtendLicenseRequest(LicenseTermDto Extension, long ExpectedVersion);

public sealed record UnbindDeviceRequest(DeviceFingerprintDto DeviceFingerprint, long ExpectedVersion);

public interface ILicenseKeyApplicationService
{
    Task<LicenseKeyDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<LicenseKeyDto>> ListAsync(
        Guid? productId,
        Guid? batchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<LicenseKeyDto> DisableAsync(Guid id, DisableLicenseRequest request, CancellationToken cancellationToken = default);

    Task<LicenseKeyDto> EnableAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default);

    Task<LicenseKeyDto> ExtendAsync(Guid id, ExtendLicenseRequest request, CancellationToken cancellationToken = default);

    Task<LicenseKeyDto> UnbindDeviceAsync(Guid id, UnbindDeviceRequest request, CancellationToken cancellationToken = default);
}
