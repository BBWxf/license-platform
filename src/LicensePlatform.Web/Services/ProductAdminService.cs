using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Domain.Products;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Services;

public sealed class ProductAdminService(
    LicensePlatformDbContext dbContext,
    IClock clock,
    IProductSigningKeyFactory signingKeyFactory,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration) : IProductAdminService
{
    public async Task<IReadOnlyList<ProductListItem>> ListAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Products
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => new ProductListItem(
                item.Id,
                item.Name,
                item.Code,
                item.LicensePrefix,
                item.IsEnabled,
                dbContext.LicenseKeys.LongCount(key => key.ProductId == item.Id),
                dbContext.DeviceBindings.LongCount(binding =>
                    binding.LicenseKey!.ProductId == item.Id && binding.UnboundAtUtc == null),
                item.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<ProductEditor?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await dbContext.Products
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new ProductEditor
            {
                Name = item.Name,
                LicensePrefix = item.LicensePrefix
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid> SaveAsync(
        Guid? id,
        ProductEditor input,
        Guid actorId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        Product product;
        string action;
        if (id is null)
        {
            product = new Product(
                Guid.NewGuid(),
                GenerateProductCode(),
                input.Name,
                input.LicensePrefix,
                360,
                now);
            dbContext.Products.Add(product);
            dbContext.ProductSigningKeys.Add(signingKeyFactory.Create(product.Id, now));
            action = "PRODUCT.CREATE";
        }
        else
        {
            product = await dbContext.Products.SingleOrDefaultAsync(item => item.Id == id.Value, cancellationToken)
                ?? throw new KeyNotFoundException("Product was not found.");
            product.Rename(input.Name, now);
            product.ChangeLicensePrefix(input.LicensePrefix, now);
            action = "PRODUCT.UPDATE";
        }

        AddAudit(actorId, action, product.Id, sourceIp, now, new
        {
            product.Code,
            product.Name,
            product.LicensePrefix
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return product.Id;
    }

    public async Task SetEnabledAsync(
        Guid id,
        bool enabled,
        Guid actorId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var product = await dbContext.Products.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Product was not found.");
        var now = clock.UtcNow;
        if (enabled)
        {
            product.Enable(now);
        }
        else
        {
            product.Disable(now);
        }

        AddAudit(actorId, enabled ? "PRODUCT.ENABLE" : "PRODUCT.DISABLE", product.Id, sourceIp, now, new { enabled });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, Guid actorId, string sourceIp, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (product is null)
        {
            return false;
        }

        var licenses = await dbContext.LicenseKeys.Where(item => item.ProductId == id).ToListAsync(cancellationToken);
        var batches = await dbContext.LicenseBatches.Where(item => item.ProductId == id).ToListAsync(cancellationToken);
        var signingKeys = await dbContext.ProductSigningKeys.Where(item => item.ProductId == id).ToListAsync(cancellationToken);

        dbContext.LicenseKeys.RemoveRange(licenses);
        dbContext.LicenseBatches.RemoveRange(batches);
        dbContext.ProductSigningKeys.RemoveRange(signingKeys);
        dbContext.Products.Remove(product);
        AddAudit(actorId, "PRODUCT.DELETE", id, sourceIp, clock.UtcNow, new
        {
            product.Code,
            product.Name,
            licenseCount = licenses.Count,
            batchCount = batches.Count
        });
        // One SaveChanges transaction deletes restricted dependents and records the audit together.
        // Database cascades remove bindings, challenges and batch export archives.
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ClientConfigDownload?> GetClientConfigAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.Id, item.Code, item.Name, item.LeaseLifetimeMinutes })
            .SingleOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return null;
        }

        var keys = await dbContext.ProductSigningKeys
            .AsNoTracking()
            .Where(item => item.ProductId == id && item.Status != SigningKeyStatus.Revoked)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => new { item.KeyId, item.Algorithm, item.PublicKeyPem, item.Status })
            .ToListAsync(cancellationToken);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = "1",
            serverBaseUrl = configuration["PublicBaseUrl"] ?? "http://SERVER_IP",
            productId = product.Id,
            productCode = product.Code,
            productName = product.Name,
            offlineLeaseMinutes = Math.Min(360, product.LeaseLifetimeMinutes),
            signingKeys = keys.Select(item => new
            {
                kid = item.KeyId,
                alg = item.Algorithm,
                pem = item.PublicKeyPem,
                status = item.Status.ToString().ToUpperInvariant()
            })
        }, new JsonSerializerOptions { WriteIndented = true });
        return new ClientConfigDownload(
            $"{product.Code.ToLowerInvariant()}-client-config.json",
            payload,
            "application/json; charset=utf-8");
    }

    private void AddAudit(
        Guid actorId,
        string action,
        Guid productId,
        string sourceIp,
        DateTimeOffset now,
        object data)
    {
        dbContext.AuditRecords.Add(new AuditRecord(
            Guid.NewGuid(),
            AuditActorType.Administrator,
            actorId.ToString("D"),
            action,
            "Product",
            productId.ToString("D"),
            JsonSerializer.Serialize(data),
            httpContextAccessor.HttpContext?.TraceIdentifier,
            sourceIp,
            now));
    }

    private static string GenerateProductCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        var builder = new StringBuilder("PRD-");
        foreach (var value in bytes)
        {
            builder.Append(alphabet[value % alphabet.Length]);
        }

        return builder.ToString();
    }
}
