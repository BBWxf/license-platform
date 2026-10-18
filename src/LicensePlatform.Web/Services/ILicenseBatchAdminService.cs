namespace LicensePlatform.Web.Services;

public interface ILicenseBatchAdminService
{
    Task<IReadOnlyList<BatchListItem>> ListAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProductOption>> GetProductOptionsAsync(CancellationToken cancellationToken);
    Task<Guid> CreateAsync(CreateBatchCommand command, Guid actorId, string sourceIp, CancellationToken cancellationToken);
    Task<BatchExportDownload?> DownloadExportAsync(Guid batchId, Guid actorId, string sourceIp, CancellationToken cancellationToken);
    Task DisableAllAsync(Guid batchId, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken);
}

public enum AdminValidityKind
{
    Day,
    Month,
    Year,
    Permanent
}

public sealed record ProductOption(Guid Id, string Name);

public sealed record BatchListItem(
    Guid Id,
    string Name,
    string ProductName,
    int RequestedCount,
    int GeneratedCount,
    AdminValidityKind ValidityKind,
    int DurationUnits,
    string Status,
    bool ExportAvailable,
    DateTimeOffset CreatedAt);

public sealed record CreateBatchCommand(
    Guid ProductId,
    string Name,
    int Count,
    AdminValidityKind ValidityKind,
    int DurationUnits,
    string? Note);

public sealed record BatchExportDownload(string FileName, byte[] Content, string ContentType);
