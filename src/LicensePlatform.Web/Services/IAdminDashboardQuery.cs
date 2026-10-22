namespace LicensePlatform.Web.Services;

public interface IAdminDashboardQuery
{
    Task<AdminDashboardSnapshot> GetAsync(CancellationToken cancellationToken);
}

public sealed record AdminDashboardSnapshot(
    int ProductCount,
    long UnusedLicenseCount,
    long ActiveLicenseCount,
    long ExpiredLicenseCount,
    long ActiveBindingCount,
    IReadOnlyList<RecentAuditItem> RecentAuditEvents);

public sealed record RecentAuditItem(
    DateTimeOffset CreatedAt,
    string Action,
    string TargetType,
    string Result,
    string? Summary)
{
    public AuditPresentation? Presentation { get; init; }
}
