namespace LicensePlatform.Web.Services;

public interface IAuditAdminService
{
    Task<AuditSearchResult> SearchAsync(int page, int pageSize, string? action, CancellationToken cancellationToken);
}

public sealed record AuditSearchResult(
    IReadOnlyList<AuditListItem> Items,
    int Page,
    int PageSize,
    long TotalCount);

public sealed record AuditListItem(
    DateTimeOffset CreatedAt,
    string Actor,
    string Action,
    string TargetType,
    string? TargetId,
    string Result,
    string SourceIp,
    string? Summary);
