using LicensePlatform.Application.Abstractions;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Services;

public sealed class AdminDashboardQuery(
    LicensePlatformDbContext dbContext,
    IClock clock) : IAdminDashboardQuery
{
    public async Task<AdminDashboardSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var productCount = await dbContext.Products.CountAsync(cancellationToken);
        var unused = await dbContext.LicenseKeys.LongCountAsync(
            item => item.Status == LicenseKeyStatus.Enabled && item.ActivatedAtUtc == null,
            cancellationToken);
        var active = await dbContext.LicenseKeys.LongCountAsync(
            item => item.Status == LicenseKeyStatus.Enabled
                    && item.ActivatedAtUtc != null
                    && (item.ExpiresAtUtc == null || item.ExpiresAtUtc > now),
            cancellationToken);
        var expired = await dbContext.LicenseKeys.LongCountAsync(
            item => item.ExpiresAtUtc != null && item.ExpiresAtUtc <= now,
            cancellationToken);
        var bindings = await dbContext.DeviceBindings.LongCountAsync(
            item => item.UnboundAtUtc == null,
            cancellationToken);
        var recentRows = await dbContext.AuditRecords
            .AsNoTracking()
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(10)
            .Select(item => new
            {
                item.OccurredAtUtc,
                item.Action,
                item.EntityType,
                item.DataJson
            })
            .ToListAsync(cancellationToken);

        // DataJson is jsonb; format summaries only after the bounded query is materialized.
        var recent = recentRows.Select(item => new RecentAuditItem(
            item.OccurredAtUtc,
            item.Action,
            item.EntityType,
            "RECORDED",
            item.DataJson.Length > 160 ? item.DataJson.Substring(0, 160) + "…" : item.DataJson)).ToList();

        return new AdminDashboardSnapshot(productCount, unused, active, expired, bindings, recent);
    }
}
