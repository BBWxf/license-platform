using LicensePlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Services;

public sealed class AuditAdminService(LicensePlatformDbContext dbContext) : IAuditAdminService
{
    public async Task<AuditSearchResult> SearchAsync(
        int page,
        int pageSize,
        string? action,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.AuditRecords.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action))
        {
            var normalized = action.Trim().ToUpperInvariant();
            query = query.Where(item => item.Action.ToUpper().Contains(normalized));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(item => item.OccurredAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new
            {
                item.OccurredAtUtc,
                item.ActorId,
                item.ActorType,
                item.Action,
                item.EntityType,
                item.EntityId,
                item.IpAddress,
                item.DataJson
            })
            .ToListAsync(cancellationToken);

        // DataJson is jsonb; format summaries only after the bounded query is materialized.
        var items = rows.Select(item => new AuditListItem(
            item.OccurredAtUtc,
            item.ActorId ?? item.ActorType.ToString(),
            item.Action,
            item.EntityType,
            item.EntityId,
            "RECORDED",
            item.IpAddress ?? "-",
            item.DataJson.Length > 220 ? item.DataJson.Substring(0, 220) + "…" : item.DataJson)
        {
            Presentation = AuditPresentationFormatter.Format(
                item.Action, item.EntityType, item.DataJson, item.ActorType, item.ActorId, item.EntityId)
        }).ToList();

        return new AuditSearchResult(items, page, pageSize, total);
    }
}
