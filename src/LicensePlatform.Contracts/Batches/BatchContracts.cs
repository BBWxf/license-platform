using LicensePlatform.Contracts.Common;

namespace LicensePlatform.Contracts.Batches;

public sealed record LicenseBatchDto(
    Guid Id,
    Guid ProductId,
    string Name,
    int Quantity,
    int IssuedCount,
    LicenseTermDto Term,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? IssuedAtUtc,
    long Version);

public sealed record CreateLicenseBatchRequest(
    Guid ProductId,
    string Name,
    int Quantity,
    LicenseTermDto Term,
    TimeSpan ExportLifetime);

public sealed record CreateLicenseBatchResult(
    LicenseBatchDto Batch,
    string ExportToken,
    DateTimeOffset ExportExpiresAtUtc);

public sealed record BatchExportDto(
    string FileName,
    string ContentType,
    byte[] Content);

public interface ILicenseBatchApplicationService
{
    Task<CreateLicenseBatchResult> CreateAsync(
        CreateLicenseBatchRequest request,
        CancellationToken cancellationToken = default);

    Task<LicenseBatchDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<LicenseBatchDto>> ListAsync(
        Guid? productId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<BatchExportDto> ConsumeExportAsync(
        string exportToken,
        CancellationToken cancellationToken = default);
}
