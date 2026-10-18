using LicensePlatform.Contracts.Common;

namespace LicensePlatform.Contracts.Auditing;

public sealed record AuditRecordDto(
    Guid Id,
    string ActorType,
    string? ActorId,
    string Action,
    string EntityType,
    string EntityId,
    string DataJson,
    string? TraceId,
    string? IpAddress,
    DateTimeOffset OccurredAtUtc);

public sealed record AuditQuery(
    string? Action,
    string? EntityType,
    string? EntityId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Page,
    int PageSize);

public interface IAuditApplicationService
{
    Task<PagedResult<AuditRecordDto>> QueryAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default);
}
