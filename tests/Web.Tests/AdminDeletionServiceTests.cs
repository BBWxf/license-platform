using System.Security.Cryptography;
using System.Text.Json;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Domain.Products;
using LicensePlatform.Infrastructure.Cryptography;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class AdminDeletionServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deletes_only_the_confirmed_scope_and_keeps_history(bool product)
    {
        await using var fixture = await Fixture.CreateAsync();
        await ExerciseDeletionAsync(fixture, product);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_target_does_not_add_a_success_audit(bool product)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.CreateContext();
        Assert.False(await fixture.DeleteAsync(context, product, Guid.NewGuid()));
        Assert.Single(await context.AuditRecords.ToListAsync());
        Assert.Equal(12, await context.LicenseKeys.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("too-long")]
    public async Task License_deletion_requires_a_bounded_reason_at_the_service_boundary(string reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.CreateContext();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.LicenseService(context).DeleteAsync(
            fixture.LicenseId, reason == "too-long" ? new string('x', 301) : reason, fixture.ActorId, "127.0.0.1", default));
        Assert.Equal(12, await context.LicenseKeys.CountAsync());
        Assert.Single(await context.AuditRecords.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_before_save_does_not_persist_deletion_or_success_audit(bool product)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var context = fixture.CreateContext(new RejectDeletionSave()))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => fixture.DeleteAsync(context, product));
        }
        await using var verify = fixture.CreateContext();
        Assert.Equal(2, await verify.Products.CountAsync());
        Assert.Equal(12, await verify.LicenseKeys.CountAsync());
        Assert.Single(await verify.AuditRecords.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stale_target_version_rejects_delete(bool product)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var stale = fixture.CreateContext();
        if (product)
        {
            await stale.Products.SingleAsync(item => item.Id == fixture.ProductId);
        }
        else
        {
            await stale.LicenseKeys.SingleAsync(item => item.Id == fixture.LicenseId);
        }
        await using (var concurrent = fixture.CreateContext())
        {
            if (product)
            {
                (await concurrent.Products.SingleAsync(item => item.Id == fixture.ProductId)).Disable(fixture.Clock.UtcNow);
            }
            else
            {
                (await concurrent.LicenseKeys.SingleAsync(item => item.Id == fixture.LicenseId)).Disable("fixture", fixture.Clock.UtcNow);
            }
            await concurrent.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => fixture.DeleteAsync(stale, product));
        // InMemory cannot prove rollback of other queued rows; relational tests below cover that.
    }

    [PostgreSqlDeletionFact]
    public Task PostgreSql_product_delete_cascades_unloaded_dependents() => RunRelationalAsync(true);

    [PostgreSqlDeletionFact]
    public Task PostgreSql_license_delete_cascades_unloaded_dependents() => RunRelationalAsync(false);

    private static async Task RunRelationalAsync(bool product)
    {
        try
        {
            await using var fixture = await Fixture.CreateAsync(relational: true);
            // A concurrent version change is detected after the service has read the graph.
            await using (var context = fixture.CreateContext(new ChangeTargetBeforeSave(fixture, product)))
            {
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => fixture.DeleteAsync(context, product));
            }
            await using (var verify = fixture.CreateContext())
            {
                Assert.Equal(2, await verify.Products.CountAsync());
                Assert.Equal(12, await verify.LicenseKeys.CountAsync());
                Assert.Equal(4, await verify.LicenseBatches.CountAsync());
                Assert.Equal(4, await verify.LicenseBatchExports.CountAsync());
                Assert.Equal(24, await verify.DeviceBindings.CountAsync());
                Assert.Equal(24, await verify.ActivationChallenges.CountAsync());
                Assert.Equal(6, await verify.ProductSigningKeys.CountAsync());
                Assert.Single(await verify.AuditRecords.ToListAsync());
            }
            await ExerciseDeletionAsync(fixture, product);
        }
        catch
        {
            Assert.Fail("PostgreSQL deletion verification failed. Check the dedicated test instance without printing configuration or provider details.");
        }
    }

    private static async Task ExerciseDeletionAsync(Fixture fixture, bool product)
    {
        await using (var context = fixture.CreateContext())
        {
            if (!context.Database.IsRelational())
            {
                // Explicitly exercise EF's tracked cascade only, not a pretend database cascade.
                await context.DeviceBindings.LoadAsync();
                await context.ActivationChallenges.LoadAsync();
                await context.LicenseBatchExports.LoadAsync();
            }
            Assert.True(await fixture.DeleteAsync(context, product));
        }
        await using var verify = fixture.CreateContext();
        Assert.Equal(product ? 1 : 2, await verify.Products.CountAsync());
        Assert.Equal(product ? 6 : 11, await verify.LicenseKeys.CountAsync());
        Assert.Equal(product ? 2 : 4, await verify.LicenseBatches.CountAsync());
        Assert.Equal(product ? 2 : 4, await verify.LicenseBatchExports.CountAsync());
        Assert.Equal(product ? 12 : 22, await verify.DeviceBindings.CountAsync());
        Assert.Equal(product ? 12 : 22, await verify.ActivationChallenges.CountAsync());
        Assert.Equal(product ? 3 : 6, await verify.ProductSigningKeys.CountAsync());
        Assert.False(await verify.LicenseKeys.AnyAsync(item => item.Id == fixture.LicenseId));
        Assert.False(await verify.ActivationChallenges.AnyAsync(item => item.LicenseKeyId == fixture.LicenseId));
        Assert.False(await verify.DeviceBindings.AnyAsync(item => item.LicenseKeyId == fixture.LicenseId));
        Assert.Equal(6, await verify.LicenseKeys.CountAsync(item => item.ProductId != fixture.ProductId));
        Assert.True(await verify.AuditRecords.AnyAsync(item => item.Id == fixture.HistoricalAuditId));
        var audit = await verify.AuditRecords.SingleAsync(item => item.Action == (product ? "PRODUCT.DELETE" : "LICENSE.DELETE"));
        Assert.Equal(fixture.ActorId.ToString("D"), audit.ActorId);
        Assert.Equal((product ? fixture.ProductId : fixture.LicenseId).ToString("D"), audit.EntityId);
        Assert.Equal("127.0.0.1", audit.IpAddress);
        using var metadata = JsonDocument.Parse(audit.DataJson);
        Assert.Equal(product ? new[] { "Code", "Name", "licenseCount", "batchCount" } : new[] { "productId", "batchId", "reason" },
            metadata.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        var presentation = AuditPresentationFormatter.Format(audit.Action, audit.EntityType, audit.DataJson);
        Assert.Equal(product ? "删除产品" : "删除卡密", presentation.ActionLabel);
        Assert.DoesNotContain("fixture deletion", presentation.Description);
        if (!product)
        {
            var archive = await verify.LicenseBatchExports.SingleAsync(item => item.BatchId == fixture.BatchId);
            Assert.True(archive.ProtectedPayload.AsSpan().SequenceEqual(fixture.ArchiveBytes));
            Assert.Equal(3, (await verify.LicenseBatches.SingleAsync(item => item.Id == fixture.BatchId)).IssuedCount);
        }
        Assert.False(await fixture.DeleteAsync(verify, product));
        Assert.Equal(2, await verify.AuditRecords.CountAsync());
    }

    private sealed class RejectDeletionSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("fixture rejected save");
    }

    private sealed class ChangeTargetBeforeSave(Fixture fixture, bool product) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await using var concurrent = fixture.CreateContext();
            if (product)
            {
                (await concurrent.Products.SingleAsync(item => item.Id == fixture.ProductId, cancellationToken)).Disable(fixture.Clock.UtcNow);
            }
            else
            {
                (await concurrent.LicenseKeys.SingleAsync(item => item.Id == fixture.LicenseId, cancellationToken)).Disable("fixture", fixture.Clock.UtcNow);
            }
            await concurrent.SaveChangesAsync(cancellationToken);
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _name = "deletion-" + Guid.NewGuid().ToString("N");
        private readonly CryptographyOptions _crypto = new()
        {
            LicenseKeyHmacKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            LookupHmacKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            DataProtectionKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        };
        private string? _connectionString;
        private string? _schema;
        public TestClock Clock { get; } = new();
        public Guid ProductId { get; private set; }
        public Guid BatchId { get; private set; }
        public Guid LicenseId { get; private set; }
        public Guid ActorId { get; } = Guid.NewGuid();
        public Guid HistoricalAuditId { get; } = Guid.NewGuid();
        public byte[] ArchiveBytes { get; } = RandomNumberGenerator.GetBytes(64);

        public static async Task<Fixture> CreateAsync(bool relational = false)
        {
            var fixture = new Fixture();
            if (relational)
            {
                var path = Environment.GetEnvironmentVariable(PostgreSqlDeletionFactAttribute.ConfigurationVariable)!;
                var file = new FileInfo(path);
                if (!file.Exists || file.Length is < 1 or > 16384)
                {
                    throw new InvalidOperationException("Invalid test configuration file.");
                }
                var builder = new NpgsqlConnectionStringBuilder((await File.ReadAllTextAsync(path)).Trim());
                if (builder.Database is null || !builder.Database.StartsWith("license_platform_test", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A dedicated test database is required.");
                }
                fixture._schema = "test_" + Guid.NewGuid().ToString("N");
                builder.SearchPath = fixture._schema;
                builder.Pooling = false;
                builder.IncludeErrorDetail = false;
                builder.LogParameters = false;
                builder.Timeout = 10;
                builder.CommandTimeout = 30;
                fixture._connectionString = builder.ConnectionString;
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                if (connection.PostgreSqlVersion.Major != 18)
                {
                    throw new InvalidOperationException("PostgreSQL 18 is required.");
                }
                await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{fixture._schema}\"", connection);
                await create.ExecuteNonQueryAsync();
            }
            try
            {
                await using var context = fixture.CreateContext();
                if (relational)
                {
                    await context.Database.MigrateAsync();
                }
                await fixture.SeedAsync(context);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public LicensePlatformDbContext CreateContext(IInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<LicensePlatformDbContext>();
            if (_connectionString is null)
            {
                builder.UseInMemoryDatabase(_name);
            }
            else
            {
                builder.UseNpgsql(_connectionString, options => options.SetPostgresVersion(18, 0).MigrationsHistoryTable("__EFMigrationsHistory", _schema));
            }
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }
            return new(builder.Options);
        }

        public LicenseAdminService LicenseService(LicensePlatformDbContext context) => new(context, Clock,
            new HmacLicenseKeyCryptography(Options.Create(_crypto)), new HttpContextAccessor());

        public Task<bool> DeleteAsync(LicensePlatformDbContext context, bool product, Guid? id = null) => product
            ? new ProductAdminService(context, Clock, new Es256ProductSigningKeyFactory(new AesGcmProtectedDataService(Options.Create(_crypto))),
                new HttpContextAccessor(), new ConfigurationBuilder().Build()).DeleteAsync(id ?? ProductId, ActorId, "127.0.0.1", default)
            : LicenseService(context).DeleteAsync(id ?? LicenseId, "fixture deletion", ActorId, "127.0.0.1", default);

        private async Task SeedAsync(LicensePlatformDbContext context)
        {
            var now = Clock.UtcNow;
            for (var p = 0; p < 2; p++)
            {
                var product = new Product(Guid.NewGuid(), "DELETE_TEST_" + p, "Deletion fixture " + p, "DEL" + p, 360, now);
                context.Products.Add(product);
                if (p == 0) ProductId = product.Id;
                for (var k = 0; k < 3; k++)
                {
                    var key = new ProductSigningKey(Guid.NewGuid(), product.Id, Guid.NewGuid().ToString("N"), "fixture-public-key", [1, 2, 3], now);
                    if (k == 1) key.Retire(now);
                    if (k == 2) key.Revoke(now);
                    context.ProductSigningKeys.Add(key);
                }
                for (var b = 0; b < 2; b++)
                {
                    var batch = new LicenseBatch(Guid.NewGuid(), product.Id, "Fixture batch", 3, LicenseTerm.Days(1), now);
                    context.LicenseBatches.Add(batch);
                    context.LicenseBatchExports.Add(new(Guid.NewGuid(), batch.Id, RandomNumberGenerator.GetBytes(32), ArchiveBytes, now, null));
                    if (p == 0 && b == 0) BatchId = batch.Id;
                    for (var l = 0; l < 3; l++)
                    {
                        var license = new LicenseKey(Guid.NewGuid(), product.Id, batch.Id, RandomNumberGenerator.GetBytes(32), "****1234", LicenseTerm.Days(1), now);
                        license.Activate(now);
                        var fingerprint = new DeviceFingerprintHashes(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
                        license.BindDevice(fingerprint, "fixture device", "fixture-public-key", now);
                        license.UnbindDevice(fingerprint, now);
                        license.BindDevice(fingerprint, "fixture device", "fixture-public-key", now);
                        if (l == 2) license.Disable("fixture", now);
                        context.LicenseKeys.Add(license);
                        for (var c = 0; c < 2; c++)
                        {
                            var challenge = new ActivationChallenge(Guid.NewGuid(), product.Id, license.Id, RandomNumberGenerator.GetBytes(32),
                                fingerprint, RandomNumberGenerator.GetBytes(32), "fixture", "fixture-public-key", ActivationChallengePurpose.Activate,
                                license.BindingVersion, now, now.AddMinutes(5));
                            if (c == 1) challenge.Consume(license.BindingVersion, now);
                            context.ActivationChallenges.Add(challenge);
                        }
                        batch.RegisterIssuedLicense(now);
                        if (p == 0 && b == 0 && l == 0) LicenseId = license.Id;
                    }
                }
            }
            context.AuditRecords.Add(new(HistoricalAuditId, AuditActorType.Administrator, ActorId.ToString("D"),
                "PRODUCT.CREATE", "Product", ProductId.ToString("D"), "{}", null, "127.0.0.1", now));
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_connectionString is null) return;
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}

public sealed class PostgreSqlDeletionFactAttribute : FactAttribute
{
    public const string ConfigurationVariable = "LICENSE_PLATFORM_TEST_DATABASE_FILE";

    public PostgreSqlDeletionFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConfigurationVariable)))
        {
            Skip = "No dedicated PostgreSQL 18 connection-secret file configured; InMemory does not verify database cascades or rollback.";
        }
    }
}
