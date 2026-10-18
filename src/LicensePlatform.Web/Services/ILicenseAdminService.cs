namespace LicensePlatform.Web.Services;

public interface ILicenseAdminService
{
    Task<LicenseSearchResult> SearchAsync(LicenseSearchQuery query, CancellationToken cancellationToken);
    Task<LicenseDetails?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task SetEnabledAsync(Guid id, bool enabled, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken);
    Task ExtendAsync(Guid id, AdminValidityKind kind, int units, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken);
    Task UnbindAsync(Guid bindingId, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken);
}

public sealed record LicenseSearchQuery(
    string? Query,
    Guid? ProductId,
    string? Status,
    int Page,
    int PageSize);

public sealed record LicenseSearchResult(
    IReadOnlyList<LicenseListItem> Items,
    int Page,
    int PageSize,
    long TotalCount,
    IReadOnlyList<ProductOption> Products);

public sealed record LicenseListItem(
    Guid Id,
    string PublicId,
    string ProductName,
    string CodeLast4,
    string Status,
    bool Enabled,
    DateTimeOffset? FirstActivatedAt,
    DateTimeOffset? ExpiresAt,
    int EntitlementVersion);

public sealed record LicenseDetails(
    Guid Id,
    string PublicId,
    string ProductName,
    string BatchName,
    string CodeLast4,
    string Status,
    bool Enabled,
    AdminValidityKind ValidityKind,
    int DurationUnits,
    DateTimeOffset? FirstActivatedAt,
    DateTimeOffset? ExpiresAt,
    int EntitlementVersion,
    string? DisabledReason,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DeviceBindingItem> Bindings);

public sealed record DeviceBindingItem(
    Guid Id,
    string PublicId,
    string State,
    string FingerprintSummary,
    DateTimeOffset ActivatedAt,
    DateTimeOffset? LastValidatedAt,
    string? LastClientVersion,
    DateTimeOffset? UnboundAt,
    string? UnbindReason);
