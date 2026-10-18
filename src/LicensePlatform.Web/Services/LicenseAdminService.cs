using System.Security.Cryptography;
using System.Text.Json;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Services;

public sealed class LicenseAdminService(
    LicensePlatformDbContext dbContext,
    IClock clock,
    ILicenseKeyCryptography licenseKeyCryptography,
    IHttpContextAccessor httpContextAccessor) : ILicenseAdminService
{
    public async Task<LicenseSearchResult> SearchAsync(
        LicenseSearchQuery search,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var query = dbContext.LicenseKeys.AsNoTracking();
        if (search.ProductId.HasValue)
        {
            query = query.Where(item => item.ProductId == search.ProductId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search.Status))
        {
            query = ApplyStatus(query, search.Status.Trim().ToUpperInvariant(), now);
        }

        if (!string.IsNullOrWhiteSpace(search.Query))
        {
            var term = search.Query.Trim();
            if (Guid.TryParse(term, out var id))
            {
                var licenseId = await dbContext.DeviceBindings
                    .AsNoTracking()
                    .Where(binding => binding.Id == id)
                    .Select(binding => (Guid?)binding.LicenseKeyId)
                    .SingleOrDefaultAsync(cancellationToken);
                query = query.Where(item => item.Id == id || item.Id == licenseId);
            }
            else
            {
                byte[]? hash = null;
                try
                {
                    hash = licenseKeyCryptography.ComputeLookupHash(term);
                }
                catch (FormatException)
                {
                    // A malformed exact search deliberately returns no rows.
                }

                query = hash is null
                    ? query.Where(_ => false)
                    : query.Where(item => item.KeyHash.SequenceEqual(hash));
            }
        }

        var page = Math.Max(1, search.Page);
        var pageSize = Math.Clamp(search.PageSize, 1, 100);
        var total = await query.LongCountAsync(cancellationToken);
        var raw = await query
            .OrderByDescending(item => item.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new
            {
                item.Id,
                item.Product!.Name,
                item.KeyHint,
                item.Status,
                item.ActivatedAtUtc,
                item.ExpiresAtUtc,
                item.BindingVersion
            })
            .ToListAsync(cancellationToken);
        var items = raw.Select(item => new LicenseListItem(
            item.Id,
            item.Id.ToString("D"),
            item.Name,
            LastFour(item.KeyHint),
            ComputeStatus(item.Status, item.ActivatedAtUtc, item.ExpiresAtUtc, now),
            item.Status == LicenseKeyStatus.Enabled,
            item.ActivatedAtUtc,
            item.ExpiresAtUtc,
            checked((int)item.BindingVersion))).ToList();
        var products = await dbContext.Products
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => new ProductOption(item.Id, item.Name))
            .ToListAsync(cancellationToken);
        return new LicenseSearchResult(items, page, pageSize, total, products);
    }

    public async Task<LicenseDetails?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var license = await dbContext.LicenseKeys
            .AsNoTracking()
            .Include(item => item.Product)
            .Include(item => item.Batch)
            .Include(item => item.DeviceBindings)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (license is null)
        {
            return null;
        }

        var now = clock.UtcNow;
        var bindings = license.DeviceBindings
            .OrderByDescending(item => item.BoundAtUtc)
            .Select(item => new DeviceBindingItem(
                item.Id,
                item.Id.ToString("D"),
                item.IsActive ? "ACTIVE" : "UNBOUND",
                BuildFingerprintSummary(item),
                item.BoundAtUtc,
                null,
                null,
                item.UnboundAtUtc,
                null))
            .ToList();
        return new LicenseDetails(
            license.Id,
            license.Id.ToString("D"),
            license.Product?.Name ?? "-",
            license.Batch?.Name ?? "-",
            LastFour(license.KeyHint),
            ComputeStatus(license.Status, license.ActivatedAtUtc, license.ExpiresAtUtc, now),
            license.Status == LicenseKeyStatus.Enabled,
            ToAdminKind(license.TermUnit),
            license.TermValue,
            license.ActivatedAtUtc,
            license.ExpiresAtUtc,
            checked((int)license.BindingVersion),
            license.DisabledReason,
            license.CreatedAtUtc,
            bindings);
    }

    public async Task SetEnabledAsync(
        Guid id,
        bool enabled,
        string reason,
        Guid actorId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var license = await dbContext.LicenseKeys.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("License was not found.");
        var now = clock.UtcNow;
        if (enabled)
        {
            license.Enable(now);
        }
        else
        {
            license.Disable(reason, now);
        }

        AddAudit(actorId, enabled ? "LICENSE.ENABLE" : "LICENSE.DISABLE", "LicenseKey", id, sourceIp, now, new
        {
            enabled,
            reason
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ExtendAsync(
        Guid id,
        AdminValidityKind kind,
        int units,
        string reason,
        Guid actorId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var license = await dbContext.LicenseKeys.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("License was not found.");
        var now = clock.UtcNow;
        var before = license.ExpiresAtUtc;
        license.Extend(kind switch
        {
            AdminValidityKind.Day => LicenseTerm.Days(units),
            AdminValidityKind.Month => LicenseTerm.Months(units),
            AdminValidityKind.Year => LicenseTerm.Years(units),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Only finite extensions are supported.")
        }, now);
        AddAudit(actorId, "LICENSE.EXTEND", "LicenseKey", id, sourceIp, now, new
        {
            kind = kind.ToString(),
            units,
            beforeExpiresAtUtc = before,
            afterExpiresAtUtc = license.ExpiresAtUtc,
            reason
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UnbindAsync(
        Guid bindingId,
        string reason,
        Guid actorId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var license = await dbContext.LicenseKeys
            .Include(item => item.DeviceBindings)
            .SingleOrDefaultAsync(item => item.DeviceBindings.Any(binding => binding.Id == bindingId), cancellationToken)
            ?? throw new KeyNotFoundException("Device binding was not found.");
        var binding = license.DeviceBindings.Single(item => item.Id == bindingId);
        if (!binding.IsActive)
        {
            throw new InvalidOperationException("Device binding has already been released.");
        }

        var now = clock.UtcNow;
        license.UnbindDevice(binding.GetFingerprint(), now);
        AddAudit(actorId, "DEVICE.UNBIND", "DeviceBinding", bindingId, sourceIp, now, new
        {
            licenseId = license.Id,
            bindingVersion = license.BindingVersion,
            reason
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void AddAudit(
        Guid actorId,
        string action,
        string entityType,
        Guid entityId,
        string sourceIp,
        DateTimeOffset now,
        object data)
    {
        dbContext.AuditRecords.Add(new AuditRecord(
            Guid.NewGuid(),
            AuditActorType.Administrator,
            actorId.ToString("D"),
            action,
            entityType,
            entityId.ToString("D"),
            JsonSerializer.Serialize(data),
            httpContextAccessor.HttpContext?.TraceIdentifier,
            sourceIp,
            now));
    }

    private static IQueryable<LicenseKey> ApplyStatus(
        IQueryable<LicenseKey> query,
        string status,
        DateTimeOffset now)
    {
        return status switch
        {
            "DISABLED" => query.Where(item => item.Status == LicenseKeyStatus.Disabled),
            "UNUSED" => query.Where(item => item.Status == LicenseKeyStatus.Enabled && item.ActivatedAtUtc == null),
            "EXPIRED" => query.Where(item => item.ExpiresAtUtc != null && item.ExpiresAtUtc <= now),
            "ACTIVE" => query.Where(item => item.Status == LicenseKeyStatus.Enabled
                                             && item.ActivatedAtUtc != null
                                             && (item.ExpiresAtUtc == null || item.ExpiresAtUtc > now)),
            _ => query
        };
    }

    private static string ComputeStatus(
        LicenseKeyStatus state,
        DateTimeOffset? activatedAt,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        if (state == LicenseKeyStatus.Disabled)
        {
            return "DISABLED";
        }

        if (!activatedAt.HasValue)
        {
            return "UNUSED";
        }

        return expiresAt.HasValue && expiresAt <= now ? "EXPIRED" : "ACTIVE";
    }

    private static string LastFour(string hint)
    {
        var compact = new string(hint.Where(char.IsAsciiLetterOrDigit).ToArray());
        return compact.Length <= 4 ? compact : compact[^4..];
    }

    private static string BuildFingerprintSummary(DeviceBinding binding)
    {
        return $"MG:{Convert.ToHexString(binding.MachineGuidHash.AsSpan(0, 3))} " +
               $"SM:{Convert.ToHexString(binding.SmbiosHash.AsSpan(0, 3))} " +
               $"VOL:{Convert.ToHexString(binding.SystemVolumeHash.AsSpan(0, 3))}";
    }

    private static AdminValidityKind ToAdminKind(LicenseTermUnit unit) => unit switch
    {
        LicenseTermUnit.Days => AdminValidityKind.Day,
        LicenseTermUnit.Months => AdminValidityKind.Month,
        LicenseTermUnit.Years => AdminValidityKind.Year,
        LicenseTermUnit.Permanent => AdminValidityKind.Permanent,
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };
}
