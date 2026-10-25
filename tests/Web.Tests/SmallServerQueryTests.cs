using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Domain.Products;
using LicensePlatform.Domain.Settings;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class SmallServerQueryTests
{
    [Fact]
    public async Task Dashboard_counts_preserve_status_overlap_and_expiration_boundaries_without_tracking()
    {
        var clock = new TestClock();
        await using var database = CreateDatabase();
        var product = new Product(Guid.NewGuid(), "SMALL_SERVER", "Small server fixture", "SMALL", 360, clock.UtcNow);
        var batch = new LicenseBatch(Guid.NewGuid(), product.Id, "Fixture", 8, LicenseTerm.Days(1), clock.UtcNow);
        database.AddRange(product, batch);
        for (var index = 0; index < 8; index++)
        {
            var term = index == 7 ? LicenseTerm.Permanent() : LicenseTerm.Days(1);
            var license = new LicenseKey(Guid.NewGuid(), product.Id, batch.Id, RandomNumberGenerator.GetBytes(32), "****1234", term, clock.UtcNow.AddDays(-2));
            if (index >= 2)
            {
                license.Activate(index switch
                {
                    2 => clock.UtcNow.AddDays(-1).AddTicks(1),
                    3 or 6 => clock.UtcNow.AddDays(-1),
                    4 => clock.UtcNow.AddDays(-2),
                    _ => clock.UtcNow
                });
            }
            if (index is 1 or 5 or 6)
            {
                license.Disable("fixture", clock.UtcNow);
            }
            if (index == 7)
            {
                var fingerprint = new DeviceFingerprintHashes(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
                license.BindDevice(fingerprint, "fixture", "fixture-public-key", clock.UtcNow);
                license.UnbindDevice(fingerprint, clock.UtcNow);
                license.BindDevice(fingerprint, "fixture", "fixture-public-key", clock.UtcNow);
            }
            database.LicenseKeys.Add(license);
        }
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var result = await new AdminDashboardQuery(database, clock).GetAsync(default);

        Assert.Equal(1, result.ProductCount);
        Assert.Equal(1, result.UnusedLicenseCount);
        Assert.Equal(2, result.ActiveLicenseCount);
        Assert.Equal(3, result.ExpiredLicenseCount); // Disabled-but-expired cards still count as expired.
        Assert.Equal(1, result.ActiveBindingCount);
        Assert.Empty(database.ChangeTracker.Entries());

        clock.Advance(TimeSpan.FromTicks(1));
        var next = await new AdminDashboardQuery(database, clock).GetAsync(default);
        Assert.Equal(1, next.ActiveLicenseCount);
        Assert.Equal(4, next.ExpiredLicenseCount);
    }

    [Fact]
    public async Task Actual_dashboard_uses_one_license_aggregate_and_four_total_queries_translatable_by_postgresql()
    {
        var capture = new QueryCapture();
        var clock = new TestClock();
        await using var database = CreateDatabase(capture);

        var result = await new AdminDashboardQuery(database, clock).GetAsync(default);

        Assert.Equal(0, result.UnusedLicenseCount);
        Assert.Equal(0, result.ActiveLicenseCount);
        Assert.Equal(0, result.ExpiredLicenseCount);
        Assert.Equal(4, capture.Queries.Count);
        var expression = Assert.Single(capture.Queries, query => HasRoot<LicenseKey>(query));
        var sql = GenerateSql<LicenseKey>(expression, clock.UtcNow);
        Assert.Contains("count(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FILTER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("activated_at_utc", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires_at_utc", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key_hash", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key_hint", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, sql.Split("license_keys", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_metadata_does_not_materialize_the_logo_payload(bool hasLogo)
    {
        var capture = new QueryCapture();
        var clock = new TestClock();
        await using var database = CreateDatabase(capture);
        database.ApplicationSettings.Add(new(Guid.NewGuid(), "branding.system-name", "\"Fixture brand\"", false, clock.UtcNow));
        if (hasLogo)
        {
            database.ApplicationSettings.Add(new(Guid.NewGuid(), "branding.logo", JsonSerializer.Serialize(new
            {
                ContentType = "image/png",
                DataBase64 = new string('A', 512 * 1024)
            }), false, clock.UtcNow));
        }
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        capture.Queries.Clear();
        var settings = SettingsService(database, clock);

        var result = await settings.GetAsync(default);

        Assert.Equal("Fixture brand", result.SystemName);
        Assert.Equal(hasLogo, result.HasLogo);
        Assert.Empty(database.ChangeTracker.Entries());
        var expression = Assert.Single(capture.Queries);
        var sql = GenerateSql<ApplicationSetting>(expression, clock.UtcNow);
        Assert.Contains("CASE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("branding.system-name", sql, StringComparison.Ordinal);
        Assert.Contains("THEN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("value_json", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is_secret", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updated_at_utc", sql, StringComparison.OrdinalIgnoreCase);
        // Execute the actual projection on the same fixtures: the logo row must contain no payload.
        var projected = (System.Collections.IEnumerable)database.ApplicationSettings.AsQueryable().Provider.CreateQuery(expression);
        foreach (var row in projected)
        {
            var properties = row!.GetType().GetProperties();
            var key = properties.Single(property => property.Name == "Key").GetValue(row);
            if (Equals(key, "branding.logo"))
            {
                Assert.Null(properties.Single(property => property.Name == "ValueJson").GetValue(row));
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("malformed")]
    public async Task Settings_preserve_fallback_and_logo_download(string? storedName)
    {
        var clock = new TestClock();
        await using var database = CreateDatabase();
        if (storedName is not null)
        {
            database.ApplicationSettings.Add(new(Guid.NewGuid(), "branding.system-name", storedName, false, clock.UtcNow));
        }
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10];
        database.ApplicationSettings.Add(new(Guid.NewGuid(), "branding.logo", JsonSerializer.Serialize(new
        {
            ContentType = "image/png",
            DataBase64 = Convert.ToBase64String(png)
        }), false, clock.UtcNow));
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var service = SettingsService(database, clock);

        var metadata = await service.GetAsync(default);
        var logo = await service.GetLogoAsync(default);

        Assert.Equal("Configured fallback", metadata.SystemName);
        Assert.True(metadata.HasLogo);
        Assert.NotNull(logo);
        Assert.Equal("image/png", logo.ContentType);
        Assert.Equal(png, logo.Content);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    private static SystemSettingsService SettingsService(LicensePlatformDbContext database, TestClock clock) => new(database, clock,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Branding:SystemName"] = "Configured fallback" }).Build(),
        new HttpContextAccessor());

    private static LicensePlatformDbContext CreateDatabase(QueryCapture? capture = null)
    {
        var options = new DbContextOptionsBuilder<LicensePlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N"));
        if (capture is not null)
        {
            options.EnableServiceProviderCaching(false).AddInterceptors(capture);
        }
        return new(options.Options);
    }

    private static bool HasRoot<T>(Expression expression)
    {
        var finder = new RootFinder(typeof(T));
        finder.Visit(expression);
        return finder.Found;
    }

    private static string GenerateSql<T>(Expression captured, DateTimeOffset now) where T : class
    {
        using var database = new LicensePlatformDbContext(new DbContextOptionsBuilder<LicensePlatformDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=small_server_sql_only;Username=sql_only").Options);
        var root = database.Set<T>().AsQueryable();
        var expression = new QueryRebinder(typeof(T), root.Expression, now).Visit(captured)!;
        // ToQueryString needs IQueryable; remove only the scalar terminal, retaining the actual aggregate.
        if (expression is MethodCallExpression terminal && terminal.Method.Name == nameof(Queryable.SingleOrDefault))
        {
            expression = terminal.Arguments[0];
        }
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        var sql = root.Provider.CreateQuery(expression).ToQueryString();
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        return sql;
    }

    private sealed class QueryCapture : IQueryExpressionInterceptor
    {
        public List<Expression> Queries { get; } = [];
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            Queries.Add(queryExpression);
            return queryExpression;
        }
    }

    private sealed class RootFinder(Type type) : ExpressionVisitor
    {
        public bool Found { get; private set; }
        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root && root.EntityType.ClrType == type) Found = true;
            return base.VisitExtension(node);
        }
    }

    private sealed class QueryRebinder(Type type, Expression replacement, DateTimeOffset now) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root && root.EntityType.ClrType == type) return replacement;
            if (node is QueryParameterExpression parameter)
            {
                if (parameter.Type == typeof(DateTimeOffset)) return Expression.Constant(now);
                if (parameter.Type == typeof(DateTimeOffset?)) return Expression.Constant((DateTimeOffset?)now, typeof(DateTimeOffset?));
                throw new InvalidOperationException("Unexpected parameter in production query.");
            }
            return base.VisitExtension(node);
        }
    }
}
