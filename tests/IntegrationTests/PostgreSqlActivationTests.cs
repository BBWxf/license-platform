using System.Security.Cryptography;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Contracts.Licenses;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Domain.Products;
using LicensePlatform.Infrastructure.ApplicationServices;
using LicensePlatform.Infrastructure.Cryptography;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LicensePlatform.IntegrationTests;

public sealed class PostgreSqlActivationTests
{
    [PostgreSqlFact]
    public Task MigrationAndConcurrentActivation_AllowOnlyOneDevice() => RunIsolatedAsync(async database =>
    {
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var firstDevice = new DeviceFingerprintDto("pg-first-machine", "pg-first-smbios", "pg-first-volume");
        var secondDevice = new DeviceFingerprintDto("pg-second-machine", "pg-second-smbios", "pg-second-volume");
        ActivationChallengeDto firstChallenge;
        ActivationChallengeDto secondChallenge;
        await using (var setup = database.CreateContext())
        {
            var service = database.CreateService(setup);
            firstChallenge = await service.CreateActivationChallengeAsync(new("PG_TEST", database.Card, firstDevice, "First", firstKey.ExportSubjectPublicKeyInfoPem()));
            secondChallenge = await service.CreateActivationChallengeAsync(new("PG_TEST", database.Card, secondDevice, "Second", secondKey.ExportSubjectPublicKeyInfoPem()));
        }

        var rendezvous = new CompletionRendezvous();
        var results = await Task.WhenAll(
            CompleteAsync(database, firstChallenge, firstDevice, firstKey, rendezvous),
            CompleteAsync(database, secondChallenge, secondDevice, secondKey, rendezvous));

        Assert.Single(results, success => success);
        await using var verify = database.CreateContext();
        Assert.Single(await verify.DeviceBindings.Where(binding => binding.UnboundAtUtc == null).ToListAsync());
        Assert.Single(await verify.ActivationChallenges.Where(challenge => challenge.ConsumedAtUtc != null).ToListAsync());
        Assert.Single(await verify.AuditRecords.ToListAsync());
        Assert.Empty(await verify.Database.GetPendingMigrationsAsync());
    });

    [PostgreSqlFact]
    public Task ConcurrentChallengeConsumption_IsAtomic_AndSameDeviceCanRetryNewChallenge() => RunIsolatedAsync(async database =>
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device = new DeviceFingerprintDto("pg-machine", "pg-smbios", "pg-volume");
        ActivationChallengeDto challenge;
        await using (var setup = database.CreateContext())
        {
            challenge = await database.CreateService(setup).CreateActivationChallengeAsync(new(
                "PG_TEST", database.Card, device, "Installation", key.ExportSubjectPublicKeyInfoPem()));
        }
        var completion = Completion(challenge, device, key);
        var rendezvous = new CompletionRendezvous();
        var results = await Task.WhenAll(
            CompleteAsync(database, completion, rendezvous), CompleteAsync(database, completion, rendezvous));
        Assert.Single(results, success => success);

        await using var retryContext = database.CreateContext();
        var license = await retryContext.LicenseKeys.SingleAsync();
        var originalActivation = license.ActivatedAtUtc;
        var originalExpiration = license.ExpiresAtUtc;
        var service = database.CreateService(retryContext);
        var retryChallenge = await service.CreateActivationChallengeAsync(new(
            "PG_TEST", database.Card, device, "Installation", key.ExportSubjectPublicKeyInfoPem()));
        var retried = await service.CompleteChallengeAsync(Completion(retryChallenge, device, key));
        Assert.Equal(originalActivation, retried.ActivatedAtUtc);
        Assert.Equal(originalExpiration, retried.EntitlementExpiresAtUtc);
        Assert.Single(await retryContext.DeviceBindings.ToListAsync());
    });

    private static Task<bool> CompleteAsync(DatabaseFixture database, ActivationChallengeDto challenge,
        DeviceFingerprintDto device, ECDsa key, CompletionRendezvous rendezvous) =>
        CompleteAsync(database, Completion(challenge, device, key), rendezvous);

    private static async Task<bool> CompleteAsync(DatabaseFixture database, CompleteActivationChallengeRequest request,
        CompletionRendezvous rendezvous)
    {
        await using var context = database.CreateContext(rendezvous);
        try
        {
            await database.CreateService(context).CompleteChallengeAsync(request);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private static CompleteActivationChallengeRequest Completion(ActivationChallengeDto challenge, DeviceFingerprintDto device, ECDsa key)
    {
        var nonce = Convert.FromBase64String(challenge.Nonce.Replace('-', '+').Replace('_', '/') + "=");
        var signature = key.SignData(nonce, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new(challenge.ChallengeToken, device, Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }

    private static async Task RunIsolatedAsync(Func<DatabaseFixture, Task> test)
    {
        // Never render provider exceptions: test configuration can contain credentials.
        try
        {
            await using var database = await DatabaseFixture.CreateAsync();
            await test(database);
        }
        catch
        {
            Assert.Fail("PostgreSQL integration verification failed. Check the dedicated test instance/configuration without printing its secrets.");
        }
    }

    private sealed class CompletionRendezvous : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ActivationChallenge>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.ConsumedAtUtc.HasValue))
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                {
                    _ready.TrySetResult();
                }
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _schema;
        private readonly CryptographyOptions _cryptography = new()
        {
            LicenseKeyHmacKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            LookupHmacKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            DataProtectionKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        };

        private DatabaseFixture(string connectionString, string schema)
        {
            _connectionString = connectionString;
            _schema = schema;
        }

        public string Card { get; private set; } = "";

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var path = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConfigurationVariable)!;
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 1 or > 16384)
            {
                throw new InvalidOperationException("Invalid database test configuration file.");
            }
            var builder = new NpgsqlConnectionStringBuilder((await File.ReadAllTextAsync(path)).Trim());
            if (builder.Database is null || !builder.Database.StartsWith("license_platform_test", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Use a dedicated license_platform_test database.");
            }
            var schema = "test_" + Guid.NewGuid().ToString("N");
            builder.SearchPath = schema;
            builder.Pooling = false;
            builder.IncludeErrorDetail = false;
            builder.LogParameters = false;
            builder.Timeout = 10;
            builder.CommandTimeout = 30;
            var fixture = new DatabaseFixture(builder.ConnectionString, schema);
            await using var connection = new NpgsqlConnection(fixture._connectionString);
            await connection.OpenAsync();
            if (connection.PostgreSqlVersion.Major != 18)
            {
                throw new InvalidOperationException("PostgreSQL 18 is required for this verification.");
            }
            await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection))
            {
                await create.ExecuteNonQueryAsync();
            }
            try
            {
                await using var context = fixture.CreateContext();
                await context.Database.MigrateAsync();
                var clock = new SystemClock();
                var now = clock.UtcNow;
                var product = new Product(Guid.NewGuid(), "PG_TEST", "PostgreSQL test", "PG", 360, now);
                var batch = new LicenseBatch(Guid.NewGuid(), product.Id, "Test batch", 1, LicenseTerm.Days(1), now);
                var generated = new HmacLicenseKeyCryptography(Options.Create(fixture._cryptography)).Generate("PG");
                fixture.Card = generated.PlainText;
                var license = new LicenseKey(Guid.NewGuid(), product.Id, batch.Id, generated.LookupHash, generated.Hint, LicenseTerm.Days(1), now);
                var protection = new AesGcmProtectedDataService(Options.Create(fixture._cryptography));
                context.AddRange(product, batch, license, new Es256ProductSigningKeyFactory(protection).Create(product.Id, now));
                await context.SaveChangesAsync();
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
            var options = new DbContextOptionsBuilder<LicensePlatformDbContext>()
                .UseNpgsql(_connectionString, npgsql => npgsql.SetPostgresVersion(18, 0).MigrationsHistoryTable("__EFMigrationsHistory", _schema));
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
            return new(options.Options);
        }

        public ActivationApplicationService CreateService(LicensePlatformDbContext context)
        {
            var options = Options.Create(_cryptography);
            return new(context, new HmacLicenseKeyCryptography(options), new HmacLookupHashService(options),
                new CryptoRandomOpaqueTokenGenerator(), new Es256DeviceProofVerifier(),
                new Es256LeaseSigningService(new AesGcmProtectedDataService(options)), new SystemClock());
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public const string ConfigurationVariable = "LICENSE_PLATFORM_TEST_DATABASE_FILE";

    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConfigurationVariable)))
        {
            Skip = "No dedicated PostgreSQL 18 connection-secret file configured; this is not covered by InMemory tests.";
        }
    }
}
