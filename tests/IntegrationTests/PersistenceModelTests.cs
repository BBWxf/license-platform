using LicensePlatform.Domain.Licensing;
using LicensePlatform.Domain.Products;
using LicensePlatform.Domain.Settings;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Infrastructure.ApplicationServices;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Application.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LicensePlatform.IntegrationTests;

public sealed class PersistenceModelTests
{
    private static LicensePlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LicensePlatformDbContext>()
            .UseNpgsql("Host=localhost;Database=license_platform_model_tests;Username=unused;Password=unused")
            .Options;
        return new LicensePlatformDbContext(options);
    }

    [Fact]
    public void ActivationContract_HasConcreteInfrastructureImplementation()
    {
        Assert.True(typeof(IActivationApplicationService).IsAssignableFrom(typeof(ActivationApplicationService)));
    }

    [Fact]
    public void MutableAggregates_HaveApplicationManagedConcurrencyTokens()
    {
        using var context = CreateContext();
        var entityTypes = new[]
        {
            typeof(Product),
            typeof(ProductSigningKey),
            typeof(LicenseBatch),
            typeof(LicenseBatchExport),
            typeof(LicenseKey),
            typeof(DeviceBinding),
            typeof(ActivationChallenge),
            typeof(ApplicationSetting)
        };

        foreach (var clrType in entityTypes)
        {
            var entityType = context.Model.FindEntityType(clrType);
            Assert.NotNull(entityType);
            var version = entityType!.FindProperty("Version");
            Assert.NotNull(version);
            Assert.True(version!.IsConcurrencyToken);
            Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, version.ValueGenerated);
        }
    }

    [Fact]
    public void LookupHashesAndOneTimeResources_HaveUniqueIndexes()
    {
        using var context = CreateContext();

        AssertUniqueIndex(context, typeof(LicenseKey), nameof(LicenseKey.KeyHash));
        AssertUniqueIndex(context, typeof(ActivationChallenge), nameof(ActivationChallenge.ChallengeHash));
        AssertUniqueIndex(context, typeof(LicenseBatchExport), nameof(LicenseBatchExport.DownloadTokenHash));
        AssertUniqueIndex(context, typeof(LicenseBatchExport), nameof(LicenseBatchExport.BatchId));
    }

    [Fact]
    public void PostgreSqlCreateScript_CanBeGeneratedForTheCompleteModel()
    {
        using var context = CreateContext();

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE products", script);
        Assert.Contains("CREATE TABLE license_keys", script);
        Assert.Contains("CREATE TABLE activation_challenges", script);
        Assert.Contains("license_prefix", script);
        Assert.Contains("machine_guid_hash", script);
        Assert.Contains("smbios_hash", script);
        Assert.Contains("system_volume_hash", script);
        Assert.Contains("ux_device_bindings_one_active_per_license", script);
        Assert.Contains("ux_product_signing_keys_one_active", script);
    }

    [Fact]
    public void InitialMigration_IsDiscoverableMatchesModelAndProducesIdempotentPostgreSqlScript()
    {
        using var context = CreateContext();

        Assert.Contains("20260911151418_InitialCreate", context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges());
        var script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("__EFMigrationsHistory", script);
        Assert.Contains("20260911151418_InitialCreate", script);
        Assert.Contains("CREATE TABLE license_keys", script);
        Assert.Contains("ux_device_bindings_one_active_per_license", script);
    }

    [Fact]
    public void DatabaseModel_EnforcesOneActiveSigningKeyAndBinding()
    {
        using var context = CreateContext();

        var signingKeyIndex = context.Model.FindEntityType(typeof(ProductSigningKey))!
            .GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_product_signing_keys_one_active");
        Assert.True(signingKeyIndex.IsUnique);
        Assert.Contains("Active", signingKeyIndex.GetFilter());

        var bindingIndex = context.Model.FindEntityType(typeof(DeviceBinding))!
            .GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_device_bindings_one_active_per_license");
        Assert.True(bindingIndex.IsUnique);
        Assert.Contains("unbound_at_utc", bindingIndex.GetFilter());
    }

    [Fact]
    public void AdminCredentialStamp_MatchesOnlyTheCurrentPasswordHash()
    {
        const string originalHash = "$argon2id$v=19$m=65536,t=3,p=2$original-salt$original-hash";
        const string changedHash = "$argon2id$v=19$m=65536,t=3,p=2$changed-salt$changed-hash";

        var stamp = AdminCredentialStamp.Create(originalHash);

        Assert.True(AdminCredentialStamp.Matches(originalHash, stamp));
        Assert.False(AdminCredentialStamp.Matches(changedHash, stamp));
        Assert.False(AdminCredentialStamp.Matches(originalHash, stamp[..^1]));
    }

    private static void AssertUniqueIndex(LicensePlatformDbContext context, Type entityClrType, string propertyName)
    {
        var entityType = context.Model.FindEntityType(entityClrType);
        Assert.NotNull(entityType);
        Assert.Contains(
            entityType!.GetIndexes(),
            index => index.IsUnique
                && index.Properties.Count == 1
                && index.Properties[0].Name == propertyName);
    }
}
