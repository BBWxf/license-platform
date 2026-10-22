using System.Collections.Concurrent;
using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Domain.Products;
using LicensePlatform.Infrastructure.Cryptography;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class LicenseBatchAdminServiceTests
{
    private static readonly Guid ActorId = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private const string SourceIp = "192.0.2.15";
    private const string TraceId = "batch-export-synthetic-trace";
    private const string DownloadAction = "BATCH.EXPORT_DOWNLOAD";

    [Fact]
    public async Task Create_retains_a_nonexpiring_archive_and_repeated_downloads_after_fifteen_minutes_preserve_all_business_state()
    {
        using var fixture = new ServiceFixture();
        var seed = await fixture.CreateArchiveAsync();
        var before = await fixture.ReadBusinessStateAsync();
        await using (var database = fixture.CreateDatabase())
        {
            var archive = await database.LicenseBatchExports.AsNoTracking().SingleAsync();
            Assert.Null(archive.ExpiresAtUtc);
            Assert.Null(archive.ConsumedAtUtc);
            Assert.True(archive.ProtectedPayload.Length > 0, "Creation must retain the encrypted CSV archive.");
            Assert.Equal(3, await database.LicenseKeys.CountAsync());
            var storedKeys = await database.LicenseKeys.AsNoTracking().ToListAsync();
            Assert.True(fixture.Keys.Generated.All(generated => storedKeys.Any(key => key.KeyHash.AsSpan().SequenceEqual(generated.LookupHash))),
                "The CSV must describe the same generated licenses that were persisted.");
            var original = fixture.Protection.Inner.Unprotect(archive.ProtectedPayload, Purpose(seed.BatchId));
            try
            {
                Assert.True(original.AsSpan().SequenceEqual(seed.ExpectedCsv), "Creation must encrypt the original UTF-8 CSV under the existing batch-ID purpose.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(original);
            }
        }

        var downloadTimes = new List<DateTimeOffset>();
        foreach (var elapsed in new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(16), TimeSpan.FromDays(30) })
        {
            fixture.Clock.Advance(elapsed);
            downloadTimes.Add(fixture.Clock.UtcNow);
            await using var database = fixture.CreateDatabase();
            var service = fixture.CreateService(database);
            var listed = Assert.Single(await service.ListAsync(CancellationToken.None));
            Assert.True(listed.ExportAvailable, "A retained archive must remain available after the former fifteen-minute deadline.");
            Assert.Empty(database.ChangeTracker.Entries());

            var download = await service.DownloadExportAsync(seed.BatchId, ActorId, SourceIp, CancellationToken.None);

            AssertDownload(seed, download);
            Assert.Empty(database.ChangeTracker.Entries<LicenseBatchExport>());
            Assert.Empty(database.ChangeTracker.Entries<LicenseBatch>());
            Assert.Empty(database.ChangeTracker.Entries<LicenseKey>());
            Assert.Empty(database.ChangeTracker.Entries<Product>());
            // Use another context: a returned file must already have its audit persisted, not just tracked.
            await using var verification = fixture.CreateDatabase();
            Assert.Equal(downloadTimes.Count, await verification.AuditRecords.CountAsync(record => record.Action == DownloadAction));
            AssertUnchanged(before, await fixture.ReadBusinessStateAsync());
        }

        await AssertDownloadAuditsAsync(fixture, seed.BatchId, downloadTimes);
        Assert.True(fixture.Protection.UnprotectPurposes.All(purpose => purpose == Purpose(seed.BatchId)),
            "Every read must use the original exact license-batch-export:{batchId:D} purpose.");
        await using var finalDatabase = fixture.CreateDatabase();
        Assert.Equal(1, await finalDatabase.AuditRecords.CountAsync(record => record.Action == "BATCH.CREATE"));
    }

    [Theory]
    [InlineData("DEMO_PRODUCT", "DEMO_PRODUCT")]
    [InlineData("-FORMULA", "'-FORMULA")]
    [InlineData("legacy,\"quoted\"", "\"legacy,\"\"quoted\"\"\"")]
    [InlineData("=legacy,\"quoted\"\r\nsecond", "\"'=legacy,\"\"quoted\"\"\r\nsecond\"")]
    public async Task Creation_preserves_csv_columns_escaping_formula_safety_and_id_based_download_metadata(string storedProductCode, string expectedProductCell)
    {
        using var fixture = new ServiceFixture();
        // Legacy persisted codes exercise CSV quoting independently of current product input validation.
        var seed = await fixture.CreateArchiveAsync(storedProductCode, expectedProductCell);
        await using var database = fixture.CreateDatabase();

        var download = await fixture.CreateService(database).DownloadExportAsync(seed.BatchId, ActorId, SourceIp, CancellationToken.None);

        AssertDownload(seed, download);
        Assert.True(download!.Content.AsSpan().SequenceEqual(seed.ExpectedCsv),
            "The original header, escaped product code, generated key ordering, term columns, UTC timestamp, and UTF-8 bytes must be retained.");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Nonempty_legacy_archives_ignore_expiry_and_consumption_without_rewriting_their_metadata(bool expired, bool consumed)
    {
        using var fixture = new ServiceFixture();
        var seed = await fixture.CreateArchiveAsync();
        await using (var database = fixture.CreateDatabase())
        {
            var archive = await database.LicenseBatchExports.SingleAsync();
            database.Entry(archive).Property(item => item.CreatedAtUtc).CurrentValue = fixture.Clock.UtcNow.AddHours(-1);
            database.Entry(archive).Property(item => item.ExpiresAtUtc).CurrentValue = expired
                ? fixture.Clock.UtcNow.AddMinutes(-30)
                : fixture.Clock.UtcNow.AddMinutes(30);
            database.Entry(archive).Property(item => item.ConsumedAtUtc).CurrentValue = consumed
                ? fixture.Clock.UtcNow.AddMinutes(-45)
                : null;
            var licenses = await database.LicenseKeys.OrderBy(item => item.Id).ToListAsync();
            licenses[0].Activate(fixture.Clock.UtcNow.AddDays(-1));
            licenses[0].Disable("synthetic administrative state", fixture.Clock.UtcNow);
            licenses[1].Extend(LicenseTerm.Days(2), fixture.Clock.UtcNow);
            await database.SaveChangesAsync();
        }
        var before = await fixture.ReadBusinessStateAsync();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var database = fixture.CreateDatabase();
            var service = fixture.CreateService(database);
            Assert.True(Assert.Single(await service.ListAsync(CancellationToken.None)).ExportAvailable,
                "Nonempty legacy archives must be listed regardless of old expiry or consumed metadata.");
            var download = await service.DownloadExportAsync(seed.BatchId, ActorId, SourceIp, CancellationToken.None);
            AssertDownload(seed, download);
            Assert.Empty(database.ChangeTracker.Entries<LicenseBatchExport>());
            Assert.Empty(database.ChangeTracker.Entries<LicenseBatch>());
            Assert.Empty(database.ChangeTracker.Entries<LicenseKey>());
            AssertUnchanged(before, await fixture.ReadBusinessStateAsync());
        }

        await AssertDownloadAuditsAsync(fixture, seed.BatchId, [fixture.Clock.UtcNow, fixture.Clock.UtcNow]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty-unconsumed")]
    [InlineData("empty-consumed")]
    public async Task Missing_and_empty_archives_are_unavailable_without_decryption_or_download_audits(string archiveState)
    {
        using var fixture = new ServiceFixture();
        var seed = await fixture.CreateArchiveAsync();
        await using (var database = fixture.CreateDatabase())
        {
            var archive = await database.LicenseBatchExports.SingleAsync();
            if (archiveState == "missing")
            {
                database.Remove(archive);
            }
            else
            {
                database.Entry(archive).Property(item => item.ProtectedPayload).CurrentValue = [];
                database.Entry(archive).Property(item => item.ExpiresAtUtc).CurrentValue = fixture.Clock.UtcNow.AddHours(1);
                database.Entry(archive).Property(item => item.ConsumedAtUtc).CurrentValue = archiveState == "empty-consumed"
                    ? fixture.Clock.UtcNow
                    : null;
            }
            await database.SaveChangesAsync();
        }
        var before = await fixture.ReadBusinessStateAsync();
        await using var readDatabase = fixture.CreateDatabase();
        var service = fixture.CreateService(readDatabase);

        Assert.False(Assert.Single(await service.ListAsync(CancellationToken.None)).ExportAvailable);
        var download = await service.DownloadExportAsync(seed.BatchId, ActorId, SourceIp, CancellationToken.None);

        Assert.True(download is null, "An unavailable archive must not release a CSV download.");
        Assert.Equal(0, fixture.Protection.UnprotectCalls);
        Assert.Empty(readDatabase.ChangeTracker.Entries());
        await AssertNoDownloadAuditsAsync(fixture);
        AssertUnchanged(before, await fixture.ReadBusinessStateAsync());
    }

    [Fact]
    public async Task Unknown_batch_does_not_return_another_batch_archive()
    {
        using var fixture = new ServiceFixture();
        await fixture.CreateArchiveAsync();
        var before = await fixture.ReadBusinessStateAsync();
        await using var database = fixture.CreateDatabase();

        var download = await fixture.CreateService(database).DownloadExportAsync(Guid.NewGuid(), ActorId, SourceIp, CancellationToken.None);

        Assert.True(download is null, "An unavailable archive must not release a CSV download.");
        Assert.Equal(0, fixture.Protection.UnprotectCalls);
        Assert.Empty(database.ChangeTracker.Entries());
        await AssertNoDownloadAuditsAsync(fixture);
        AssertUnchanged(before, await fixture.ReadBusinessStateAsync());
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("truncated")]
    [InlineData("wrong-key")]
    [InlineData("wrong-purpose")]
    [InlineData("empty-plaintext")]
    public async Task Undecryptable_or_empty_plaintext_archives_fail_closed_without_erasing_the_archive_or_auditing_success(string failure)
    {
        using var fixture = new ServiceFixture();
        var seed = await fixture.CreateArchiveAsync();
        await using (var database = fixture.CreateDatabase())
        {
            var archive = await database.LicenseBatchExports.SingleAsync();
            var payload = archive.ProtectedPayload.ToArray();
            if (failure == "tampered")
            {
                payload[^1] ^= 0x40;
            }
            else if (failure == "truncated")
            {
                payload = [1];
            }
            else if (failure == "wrong-purpose")
            {
                payload = fixture.Protection.Inner.Protect(seed.ExpectedCsv, Purpose(Guid.NewGuid()));
            }
            else if (failure == "empty-plaintext")
            {
                payload = fixture.Protection.Inner.Protect([], Purpose(seed.BatchId));
            }
            database.Entry(archive).Property(item => item.ProtectedPayload).CurrentValue = payload;
            await database.SaveChangesAsync();
        }
        var before = await fixture.ReadBusinessStateAsync();
        var protection = failure == "wrong-key"
            ? new RecordingProtectedDataService(CreateProtection())
            : fixture.Protection;
        await using var readDatabase = fixture.CreateDatabase();
        var service = fixture.CreateService(readDatabase, protection);
        Assert.True(Assert.Single(await service.ListAsync(CancellationToken.None)).ExportAvailable,
            "Listing must inspect encrypted payload presence only, not pre-decrypt or hide potentially recoverable archives.");
        Assert.Equal(0, protection.UnprotectCalls);

        var download = await service.DownloadExportAsync(seed.BatchId, ActorId, SourceIp, CancellationToken.None);

        Assert.True(download is null, "An unavailable archive must not release a CSV download.");
        Assert.Equal(1, protection.UnprotectCalls);
        Assert.Empty(readDatabase.ChangeTracker.Entries());
        await AssertNoDownloadAuditsAsync(fixture);
        AssertUnchanged(before, await fixture.ReadBusinessStateAsync());
    }

    [Fact]
    public async Task Noncryptographic_decryption_failures_propagate_instead_of_being_misreported_as_missing_archives()
    {
        using var fixture = new ServiceFixture();
        var seed = await fixture.CreateArchiveAsync();
        var before = await fixture.ReadBusinessStateAsync();
        var expected = new InvalidOperationException("Synthetic noncryptographic decryption failure.");
        fixture.Protection.UnprotectFailure = expected;
        await using var database = fixture.CreateDatabase();
        BatchExportDownload? returned = null;

        var failure = await Record.ExceptionAsync(async () =>
        {
            returned = await fixture.CreateService(database).DownloadExportAsync(seed.BatchId, ActorId, SourceIp, CancellationToken.None);
        });

        Assert.True(ReferenceEquals(expected, failure), "Only cryptographic payload failures may be converted into an unavailable export.");
        Assert.True(returned is null, "A failed decryptor must not release a CSV download.");
        Assert.Empty(database.ChangeTracker.Entries());
        await AssertNoDownloadAuditsAsync(fixture);
        AssertUnchanged(before, await fixture.ReadBusinessStateAsync());
    }

    [Theory]
    [InlineData("audit-failure")]
    [InlineData("after-decryption")]
    [InlineData("during-audit-save")]
    [InlineData("after-audit-save")]
    public async Task Audit_failure_or_cancellation_never_releases_a_file_and_zeroes_the_actual_decrypted_buffer(string failurePoint)
    {
        using var fixture = new ServiceFixture();
        var seed = await fixture.CreateArchiveAsync();
        var before = await fixture.ReadBusinessStateAsync();
        using var cancellation = new CancellationTokenSource();
        if (failurePoint == "after-decryption")
        {
            fixture.Protection.AfterUnprotect = cancellation.Cancel;
        }
        var interceptor = new FailingAuditSaveInterceptor(failurePoint, cancellation);
        await using var database = fixture.CreateDatabase(interceptor);
        BatchExportDownload? returned = null;

        var failure = await Record.ExceptionAsync(async () =>
        {
            returned = await fixture.CreateService(database).DownloadExportAsync(seed.BatchId, ActorId, SourceIp, cancellation.Token);
        });

        Assert.True(failurePoint == "audit-failure" ? failure is SyntheticAuditSaveException : failure is OperationCanceledException,
            "Audit persistence failures and cancellation must propagate rather than returning un-audited CSV bytes.");
        Assert.True(returned is null, "A failed or cancelled audit must not release a CSV download.");
        Assert.Equal(1, fixture.Protection.UnprotectCalls);
        Assert.True(fixture.Protection.UnprotectedBuffers.Count == 1, "The failing read must decrypt exactly one buffer.");
        var buffer = fixture.Protection.UnprotectedBuffers[0];
        Assert.True(buffer.Length > 0 && buffer.All(value => value == 0),
            "The exact plaintext array returned by the cryptography service must be zeroed before the failure escapes.");
        Assert.Empty(database.ChangeTracker.Entries<LicenseBatchExport>());
        Assert.Empty(database.ChangeTracker.Entries<LicenseBatch>());
        Assert.Empty(database.ChangeTracker.Entries<LicenseKey>());
        if (failurePoint != "after-decryption")
        {
            Assert.True(interceptor.SawOnlyAddedDownloadAudit, "An export save must contain only a new download audit, never business-entity writes.");
        }
        await using var verification = fixture.CreateDatabase();
        Assert.Equal(failurePoint == "after-audit-save" ? 1 : 0,
            await verification.AuditRecords.CountAsync(record => record.Action == DownloadAction));
        AssertUnchanged(before, await fixture.ReadBusinessStateAsync());
    }

    [Fact]
    public async Task Separate_context_concurrent_reads_in_isolated_inmemory_storage_return_the_same_archive_and_append_independent_audits()
    {
        // This is an isolated InMemory concurrency regression, not PostgreSQL transaction/concurrency verification.
        using var fixture = new ServiceFixture();
        var seed = await fixture.CreateArchiveAsync();
        var before = await fixture.ReadBusinessStateAsync();
        const int readers = 4;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var gate = new ConcurrentAuditSaveGate(readers);
        var reads = Enumerable.Range(0, readers).Select(async _ =>
        {
            await using var database = fixture.CreateDatabase(gate);
            var result = await fixture.CreateService(database).DownloadExportAsync(seed.BatchId, ActorId, SourceIp, cancellation.Token);
            Assert.Empty(database.ChangeTracker.Entries<LicenseBatchExport>());
            Assert.Empty(database.ChangeTracker.Entries<LicenseBatch>());
            Assert.Empty(database.ChangeTracker.Entries<LicenseKey>());
            return result;
        }).ToArray();

        var downloads = await Task.WhenAll(reads);

        Assert.Equal(readers, gate.Arrivals);
        foreach (var download in downloads)
        {
            AssertDownload(seed, download);
        }
        await AssertDownloadAuditsAsync(fixture, seed.BatchId, Enumerable.Repeat(fixture.Clock.UtcNow, readers).ToArray());
        AssertUnchanged(before, await fixture.ReadBusinessStateAsync());
    }

    [Fact]
    public async Task Actual_list_expression_translates_to_postgresql_exists_and_byte_length_without_projecting_archive_payloads()
    {
        using var fixture = new ServiceFixture();
        await fixture.CreateArchiveAsync();
        var capture = new BatchListQueryCapture();
        await using var database = fixture.CreateDatabase(capture);

        await fixture.CreateService(database).ListAsync(CancellationToken.None);

        var expression = Assert.Single(capture.Queries);
        // Translation only. This fictional, password-free connection is never opened.
        var options = new DbContextOptionsBuilder<LicensePlatformDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=batch_export_sql_only;Username=sql_only").Options;
        using var relational = new LicensePlatformDbContext(options);
        var roots = new Dictionary<Type, Expression>
        {
            [typeof(LicenseBatch)] = ((IQueryable<LicenseBatch>)relational.LicenseBatches).Expression,
            [typeof(LicenseBatchExport)] = ((IQueryable<LicenseBatchExport>)relational.LicenseBatchExports).Expression
        };
        var rebound = new PostgreSqlRootRebinder(roots).Visit(expression)!;
        var query = ((IQueryable<LicenseBatch>)relational.LicenseBatches).Provider.CreateQuery(rebound);
        Assert.Equal(ConnectionState.Closed, relational.Database.GetDbConnection().State);
        var sql = query.ToQueryString();
        Assert.Equal(ConnectionState.Closed, relational.Database.GetDbConnection().State);
        Assert.Contains("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("license_batch_exports", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("(?i)\\b(?:octet_)?length\\s*\\(", sql);
        Assert.DoesNotContain("expires_at_utc", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consumed_at_utc", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("download_token_hash", sql, StringComparison.OrdinalIgnoreCase);
        // PostgreSQL length(bytea) and octet_length(bytea) both count bytes; the provider may use either.
        // The sole payload reference must be inside that function, not a selected bytea column.
        var payloadReferences = Regex.Matches(sql, "\\bprotected_payload\\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        var lengthReferences = Regex.Matches(sql, "\\b(?:octet_)?length\\s*\\(\\s*(?:[\\w\"]+\\.)?\"?protected_payload\"?\\s*\\)",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        Assert.Single(payloadReferences.Cast<Match>());
        Assert.Single(lengthReferences.Cast<Match>());
        Assert.Empty(database.ChangeTracker.Entries());
    }

    private static string Purpose(Guid batchId) => $"license-batch-export:{batchId:D}";

    private static void AssertDownload(ArchiveSeed seed, BatchExportDownload? download)
    {
        Assert.NotNull(download);
        Assert.Equal($"license-batch-{seed.BatchId:N}.csv", download.FileName);
        Assert.Equal("text/csv; charset=utf-8", download.ContentType);
        // Do not pass generated keys or CSV to expected/actual assertions: even a failure stays secret-safe.
        Assert.True(download.Content.AsSpan().SequenceEqual(seed.ExpectedCsv), "The retained CSV must be byte-identical on every successful download.");
    }

    private static async Task AssertNoDownloadAuditsAsync(ServiceFixture fixture)
    {
        await using var database = fixture.CreateDatabase();
        Assert.Equal(0, await database.AuditRecords.CountAsync(record => record.Action == DownloadAction));
    }

    private static async Task AssertDownloadAuditsAsync(ServiceFixture fixture, Guid batchId, IReadOnlyList<DateTimeOffset> expectedTimes)
    {
        await using var database = fixture.CreateDatabase();
        var audits = await database.AuditRecords.AsNoTracking().Where(record => record.Action == DownloadAction)
            .OrderBy(record => record.OccurredAtUtc).ToListAsync();
        Assert.Equal(expectedTimes.Count, audits.Count);
        Assert.Equal(expectedTimes.Order(), audits.Select(record => record.OccurredAtUtc));
        Assert.Equal(audits.Count, audits.Select(record => record.Id).Distinct().Count());
        foreach (var audit in audits)
        {
            Assert.Equal(AuditActorType.Administrator, audit.ActorType);
            Assert.Equal(ActorId.ToString("D"), audit.ActorId);
            Assert.Equal("LicenseBatch", audit.EntityType);
            Assert.Equal(batchId.ToString("D"), audit.EntityId);
            Assert.Equal(SourceIp, audit.IpAddress);
            Assert.Equal(TraceId, audit.TraceId);
            using var data = JsonDocument.Parse(audit.DataJson);
            var fields = data.RootElement.EnumerateObject().ToArray();
            Assert.True(fields.Length == 1 && fields[0].Name == "repeatable" && fields[0].Value.ValueKind == JsonValueKind.True,
                "Download audits must contain only repeatable=true, never plaintext, tokens, or one-time metadata.");
        }
    }

    private static void AssertUnchanged(IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after)
    {
        Assert.True(before.Count == after.Count && before.Keys.All(after.ContainsKey), "Downloading must not add or remove business entities or mapped state.");
        foreach (var property in before)
        {
            var same = property.Value is byte[] bytes
                ? after[property.Key] is byte[] actual && bytes.AsSpan().SequenceEqual(actual)
                : Equals(property.Value, after[property.Key]);
            Assert.True(same, "Downloading must not change persisted archive, batch, license, binding, product, or version state.");
        }
    }

    private static AesGcmProtectedDataService CreateProtection() => new(Options.Create(NewCryptographyOptions()));

    private static CryptographyOptions NewCryptographyOptions() => new()
    {
        DataProtectionKeyBase64 = NewEncodedKey(),
        LicenseKeyHmacKeyBase64 = NewEncodedKey(),
        LookupHmacKeyBase64 = NewEncodedKey()
    };

    private static string NewEncodedKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try { return Convert.ToBase64String(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed record ArchiveSeed(Guid BatchId, byte[] ExpectedCsv);

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _databaseName = "batch-export-service-" + Guid.NewGuid().ToString("N");
        private readonly InMemoryDatabaseRoot _root = new();
        private readonly HmacLookupHashService _lookup;
        private readonly List<byte[]> _expectedBuffers = [];
        public TestClock Clock { get; } = new();
        public RecordingLicenseKeyCryptography Keys { get; }
        public RecordingProtectedDataService Protection { get; }

        public ServiceFixture()
        {
            var options = Options.Create(NewCryptographyOptions());
            Keys = new RecordingLicenseKeyCryptography(new HmacLicenseKeyCryptography(options));
            Protection = new RecordingProtectedDataService(new AesGcmProtectedDataService(options));
            _lookup = new HmacLookupHashService(options);
        }

        public LicensePlatformDbContext CreateDatabase(params IInterceptor[] interceptors)
        {
            var options = new DbContextOptionsBuilder<LicensePlatformDbContext>().UseInMemoryDatabase(_databaseName, _root);
            if (interceptors.Length > 0)
            {
                options.EnableServiceProviderCaching(false).AddInterceptors(interceptors);
            }
            return new LicensePlatformDbContext(options.Options);
        }

        public LicenseBatchAdminService CreateService(LicensePlatformDbContext database, IProtectedDataService? protection = null) => new(
            database, Clock, Keys, protection ?? Protection, new CryptoRandomOpaqueTokenGenerator(), _lookup,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { TraceIdentifier = TraceId } });

        public async Task<ArchiveSeed> CreateArchiveAsync(string storedProductCode = "DEMO_PRODUCT", string expectedProductCell = "DEMO_PRODUCT")
        {
            await using var database = CreateDatabase();
            var product = new Product(Guid.NewGuid(), "DEMO_PRODUCT", "Synthetic export product", "DEMO", 5, Clock.UtcNow);
            database.Products.Add(product);
            database.Entry(product).Property(item => item.Code).CurrentValue = storedProductCode;
            await database.SaveChangesAsync();
            var now = Clock.UtcNow;
            var firstGenerated = Keys.Generated.Count;
            var batchId = await CreateService(database).CreateAsync(
                new CreateBatchCommand(product.Id, "Synthetic export batch", 3, AdminValidityKind.Month, 3, "synthetic note"),
                ActorId, SourceIp, CancellationToken.None);
            var expected = new StringBuilder();
            expected.AppendLine("product_code,batch_id,license_key,validity_kind,validity_units,created_at_utc");
            foreach (var generated in Keys.Generated.Skip(firstGenerated))
            {
                expected.Append(expectedProductCell).Append(',').Append(batchId.ToString("D")).Append(',')
                    .Append(generated.PlainText).Append(",MONTH,3,").Append(now.ToString("O")).AppendLine();
            }
            var csv = Encoding.UTF8.GetBytes(expected.ToString());
            _expectedBuffers.Add(csv);
            return new ArchiveSeed(batchId, csv);
        }

        public async Task<IReadOnlyDictionary<string, object?>> ReadBusinessStateAsync()
        {
            await using var database = CreateDatabase();
            var values = new Dictionary<string, object?>();
            Add(await database.Products.AsNoTracking().ToListAsync());
            Add(await database.LicenseBatches.AsNoTracking().ToListAsync());
            Add(await database.LicenseBatchExports.AsNoTracking().ToListAsync());
            Add(await database.LicenseKeys.AsNoTracking().ToListAsync());
            Add(await database.DeviceBindings.AsNoTracking().ToListAsync());
            return values;

            void Add<T>(IEnumerable<T> entities) where T : Entity
            {
                foreach (var entity in entities)
                {
                    foreach (var property in database.Entry(entity).Properties)
                    {
                        values.Add(typeof(T).Name + "/" + entity.Id.ToString("D") + "/" + property.Metadata.Name,
                            property.CurrentValue is byte[] bytes ? bytes.ToArray() : property.CurrentValue);
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var buffer in _expectedBuffers.Concat(Protection.UnprotectedBuffers))
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private sealed class RecordingLicenseKeyCryptography(ILicenseKeyCryptography inner) : ILicenseKeyCryptography
    {
        public List<GeneratedLicenseKey> Generated { get; } = [];
        public GeneratedLicenseKey Generate(string licensePrefix)
        {
            var generated = inner.Generate(licensePrefix);
            Generated.Add(generated);
            return generated;
        }
        public string Normalize(string value) => inner.Normalize(value);
        public string GetPrefix(string value) => inner.GetPrefix(value);
        public byte[] ComputeLookupHash(string value) => inner.ComputeLookupHash(value);
    }

    private sealed class RecordingProtectedDataService(IProtectedDataService inner) : IProtectedDataService
    {
        private int _unprotectCalls;
        private readonly ConcurrentQueue<byte[]> _buffers = new();
        private readonly ConcurrentQueue<string> _purposes = new();
        public IProtectedDataService Inner => inner;
        public Action? AfterUnprotect { get; set; }
        public Exception? UnprotectFailure { get; set; }
        public int UnprotectCalls => Volatile.Read(ref _unprotectCalls);
        public IReadOnlyList<byte[]> UnprotectedBuffers => _buffers.ToArray();
        public IReadOnlyList<string> UnprotectPurposes => _purposes.ToArray();
        public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose) => inner.Protect(plaintext, purpose);
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData, string purpose)
        {
            Interlocked.Increment(ref _unprotectCalls);
            _purposes.Enqueue(purpose);
            if (UnprotectFailure is { } failure)
            {
                throw failure;
            }
            var plaintext = inner.Unprotect(protectedData, purpose);
            _buffers.Enqueue(plaintext);
            AfterUnprotect?.Invoke();
            return plaintext;
        }
    }

    private sealed class SyntheticAuditSaveException : Exception { }

    private sealed class FailingAuditSaveInterceptor(string failurePoint, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public bool SawOnlyAddedDownloadAudit { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToArray();
            SawOnlyAddedDownloadAudit = entries.Length == 1 && entries[0].Entity is AuditRecord audit
                && audit.Action == DownloadAction && entries[0].State == EntityState.Added;
            if (failurePoint == "audit-failure")
            {
                throw new SyntheticAuditSaveException();
            }
            if (failurePoint == "during-audit-save")
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (failurePoint == "after-audit-save")
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ConcurrentAuditSaveGate(int participants) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) == participants)
            {
                _allArrived.TrySetResult();
            }
            await _allArrived.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class BatchListQueryCapture : IQueryExpressionInterceptor
    {
        public List<Expression> Queries { get; } = [];
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            if (queryExpression.Type == typeof(IQueryable<BatchListItem>))
            {
                Queries.Add(queryExpression);
            }
            return queryExpression;
        }
    }

    private sealed class PostgreSqlRootRebinder(IReadOnlyDictionary<Type, Expression> roots) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root && roots.TryGetValue(root.EntityType.ClrType, out var replacement))
            {
                return replacement;
            }
            if (node is QueryParameterExpression)
            {
                throw new InvalidOperationException("The retained-archive list query must not have an unbound lifetime parameter.");
            }
            return base.VisitExtension(node);
        }
    }
}
