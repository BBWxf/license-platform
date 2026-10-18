using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Services;

public sealed class LicenseBatchAdminService(
    LicensePlatformDbContext dbContext,
    IClock clock,
    ILicenseKeyCryptography licenseKeyCryptography,
    IProtectedDataService protectedDataService,
    IOpaqueTokenGenerator tokenGenerator,
    ILookupHashService lookupHashService,
    IHttpContextAccessor httpContextAccessor) : ILicenseBatchAdminService
{
    private static readonly TimeSpan ExportLifetime = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<BatchListItem>> ListAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return await dbContext.LicenseBatches
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => new BatchListItem(
                item.Id,
                item.Name,
                item.Product!.Name,
                item.Quantity,
                item.IssuedCount,
                ToAdminKind(item.TermUnit),
                item.TermValue,
                item.Status.ToString().ToUpperInvariant(),
                dbContext.LicenseBatchExports.Any(export =>
                    export.BatchId == item.Id
                    && export.ConsumedAtUtc == null
                    && export.ExpiresAtUtc > now),
                item.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProductOption>> GetProductOptionsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Products
            .AsNoTracking()
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .Select(item => new ProductOption(item.Id, item.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> CreateAsync(
        CreateBatchCommand command,
        Guid actorId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        if (command.Count is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(command.Count), "Batch count must be between 1 and 10,000.");
        }

        var product = await dbContext.Products
            .SingleOrDefaultAsync(item => item.Id == command.ProductId && item.IsEnabled, cancellationToken)
            ?? throw new KeyNotFoundException("Enabled product was not found.");
        var now = clock.UtcNow;
        var term = ToDomainTerm(command.ValidityKind, command.DurationUnits);
        var batch = new LicenseBatch(Guid.NewGuid(), product.Id, command.Name, command.Count, term, now);
        dbContext.LicenseBatches.Add(batch);

        var csv = new StringBuilder(command.Count * 80);
        csv.AppendLine("product_code,batch_id,license_key,validity_kind,validity_units,created_at_utc");
        for (var index = 0; index < command.Count; index++)
        {
            var generated = licenseKeyCryptography.Generate(product.LicensePrefix);
            dbContext.LicenseKeys.Add(new LicenseKey(
                Guid.NewGuid(),
                product.Id,
                batch.Id,
                generated.LookupHash,
                generated.Hint,
                term,
                now));
            batch.RegisterIssuedLicense(now);
            csv.Append(EscapeCsv(product.Code)).Append(',')
                .Append(batch.Id.ToString("D")).Append(',')
                .Append(EscapeCsv(generated.PlainText)).Append(',')
                .Append(command.ValidityKind.ToString().ToUpperInvariant()).Append(',')
                .Append(command.DurationUnits).Append(',')
                .Append(now.ToString("O")).AppendLine();
        }

        var plaintext = Encoding.UTF8.GetBytes(csv.ToString());
        var token = tokenGenerator.Generate();
        try
        {
            dbContext.LicenseBatchExports.Add(new LicenseBatchExport(
                Guid.NewGuid(),
                batch.Id,
                lookupHashService.ComputeExportTokenHash(token),
                protectedDataService.Protect(plaintext, ExportPurpose(batch.Id)),
                now,
                now.Add(ExportLifetime)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        AddAudit(actorId, "BATCH.CREATE", "LicenseBatch", batch.Id, sourceIp, now, new
        {
            productId = product.Id,
            command.Count,
            validity = command.ValidityKind.ToString(),
            command.DurationUnits,
            note = SanitizeCsvText(command.Note)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return batch.Id;
    }

    public async Task<BatchExportDownload?> DownloadExportAsync(
        Guid batchId,
        Guid actorId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var export = await dbContext.LicenseBatchExports
            .Include(item => item.Batch)
            .SingleOrDefaultAsync(item => item.BatchId == batchId, cancellationToken);
        var now = clock.UtcNow;
        if (export is null || export.IsConsumed || export.ExpiresAtUtc <= now)
        {
            return null;
        }

        var plaintext = protectedDataService.Unprotect(export.ProtectedPayload, ExportPurpose(batchId));
        export.Consume(now);
        export.ErasePayload();
        AddAudit(actorId, "BATCH.EXPORT_DOWNLOAD", "LicenseBatch", batchId, sourceIp, now, new { once = true });
        await dbContext.SaveChangesAsync(cancellationToken);
        return new BatchExportDownload(
            $"license-batch-{batchId:N}.csv",
            plaintext,
            "text/csv; charset=utf-8");
    }

    public async Task DisableAllAsync(
        Guid batchId,
        string reason,
        Guid actorId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var licenses = await dbContext.LicenseKeys
            .Where(item => item.BatchId == batchId)
            .ToListAsync(cancellationToken);
        if (licenses.Count == 0)
        {
            throw new KeyNotFoundException("Batch was not found or contains no licenses.");
        }

        var now = clock.UtcNow;
        foreach (var license in licenses)
        {
            license.Disable(reason, now);
        }

        AddAudit(actorId, "BATCH.DISABLE_ALL", "LicenseBatch", batchId, sourceIp, now, new
        {
            count = licenses.Count,
            reason = SanitizeCsvText(reason)
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

    private static LicenseTerm ToDomainTerm(AdminValidityKind kind, int units)
    {
        return kind switch
        {
            AdminValidityKind.Day => LicenseTerm.Days(units),
            AdminValidityKind.Month => LicenseTerm.Months(units),
            AdminValidityKind.Year => LicenseTerm.Years(units),
            AdminValidityKind.Permanent => LicenseTerm.Permanent(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static AdminValidityKind ToAdminKind(LicenseTermUnit unit)
    {
        return unit switch
        {
            LicenseTermUnit.Days => AdminValidityKind.Day,
            LicenseTermUnit.Months => AdminValidityKind.Month,
            LicenseTermUnit.Years => AdminValidityKind.Year,
            LicenseTermUnit.Permanent => AdminValidityKind.Permanent,
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
    }

    private static string ExportPurpose(Guid batchId) => $"license-batch-export:{batchId:D}";

    private static string EscapeCsv(string value)
    {
        value = SanitizeCsvText(value) ?? string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static string? SanitizeCsvText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@'
            ? "'" + trimmed
            : trimmed;
    }
}
