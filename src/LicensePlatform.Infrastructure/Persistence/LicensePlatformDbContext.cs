using LicensePlatform.Domain.Administration;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Domain.Products;
using LicensePlatform.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Infrastructure.Persistence;

public sealed class LicensePlatformDbContext : DbContext
{
    public LicensePlatformDbContext(DbContextOptions<LicensePlatformDbContext> options)
        : base(options)
    {
    }

    public DbSet<Administrator> Administrators => Set<Administrator>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductSigningKey> ProductSigningKeys => Set<ProductSigningKey>();

    public DbSet<LicenseBatch> LicenseBatches => Set<LicenseBatch>();

    public DbSet<LicenseBatchExport> LicenseBatchExports => Set<LicenseBatchExport>();

    public DbSet<LicenseKey> LicenseKeys => Set<LicenseKey>();

    public DbSet<DeviceBinding> DeviceBindings => Set<DeviceBinding>();

    public DbSet<ActivationChallenge> ActivationChallenges => Set<ActivationChallenge>();

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditRecordsAreAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAuditRecordsAreAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LicensePlatformDbContext).Assembly);
    }

    private void EnsureAuditRecordsAreAppendOnly()
    {
        if (ChangeTracker.Entries<AuditRecord>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit records are append-only and cannot be updated or deleted.");
        }
    }
}
