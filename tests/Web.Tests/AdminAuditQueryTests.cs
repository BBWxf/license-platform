using System.Data;
using System.Linq.Expressions;
using System.Text.Json;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class AdminAuditQueryTests
{
    [Fact]
    public async Task Dashboard_returns_only_the_ten_most_recent_audits_in_descending_order_without_tracking()
    {
        var clock = new TestClock();
        await using var database = CreateDatabase();
        var records = Enumerable.Range(0, 13)
            .Select(index => CreateRecord(clock, index, JsonOfLength(24)))
            .ToArray();
        await SeedAsync(database, records.Reverse());

        var snapshot = await new AdminDashboardQuery(database, clock).GetAsync(CancellationToken.None);

        Assert.Equal(records.Take(10).Select(record => record.OccurredAtUtc),
            snapshot.RecentAuditEvents.Select(item => item.CreatedAt));
        Assert.Equal(records.Take(10).Select(record => record.Action),
            snapshot.RecentAuditEvents.Select(item => item.Action));
        Assert.Equal(10, snapshot.RecentAuditEvents.Count);
        Assert.All(snapshot.RecentAuditEvents, item =>
        {
            Assert.Equal("LicenseKey", item.TargetType);
            Assert.Equal("RECORDED", item.Result);
            Assert.Equal(JsonOfLength(24), item.Summary);
        });
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(160)]
    [InlineData(161)]
    public async Task Dashboard_preserves_short_and_exact_limit_json_and_truncates_only_over_160_characters(int length)
    {
        var clock = new TestClock();
        var dataJson = JsonOfLength(length);
        await using var database = CreateDatabase();
        await SeedAsync(database, [CreateRecord(clock, 0, dataJson)]);

        var snapshot = await new AdminDashboardQuery(database, clock).GetAsync(CancellationToken.None);

        var item = Assert.Single(snapshot.RecentAuditEvents);
        Assert.Equal(length > 160 ? dataJson[..160] + "…" : dataJson, item.Summary);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Dashboard_handles_an_empty_database_without_tracking()
    {
        await using var database = CreateDatabase();

        var snapshot = await new AdminDashboardQuery(database, new TestClock()).GetAsync(CancellationToken.None);

        Assert.Empty(snapshot.RecentAuditEvents);
        Assert.Equal(0, snapshot.ProductCount);
        Assert.Equal(0L, snapshot.UnusedLicenseCount);
        Assert.Equal(0L, snapshot.ActiveLicenseCount);
        Assert.Equal(0L, snapshot.ExpiredLicenseCount);
        Assert.Equal(0L, snapshot.ActiveBindingCount);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Audit_search_filters_before_paging_and_returns_sorted_rows_total_and_fallback_fields_without_tracking()
    {
        var clock = new TestClock();
        await using var database = CreateDatabase();
        var records = Enumerable.Range(0, 14)
            .Select(index => CreateRecord(clock, index, JsonOfLength(24),
                index % 2 == 0 ? "License.AcTiVaTe.completed" : "License.Disabled"))
            .ToArray();
        await SeedAsync(database, records.Reverse());

        var result = await new AuditAdminService(database).SearchAsync(2, 3, "  aCtIvAtE  ", CancellationToken.None);

        Assert.Equal(2, result.Page);
        Assert.Equal(3, result.PageSize);
        Assert.Equal(7L, result.TotalCount);
        var expected = new[] { records[6], records[8], records[10] };
        Assert.Collection(result.Items,
            item => AssertAuditItem(expected[0], item),
            item => AssertAuditItem(expected[1], item),
            item => AssertAuditItem(expected[2], item));
        Assert.Equal("System", result.Items[0].Actor);
        Assert.Equal("-", result.Items[1].SourceIp);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(220)]
    [InlineData(221)]
    public async Task Audit_search_preserves_short_and_exact_limit_json_and_truncates_only_over_220_characters(int length)
    {
        var clock = new TestClock();
        var dataJson = JsonOfLength(length);
        await using var database = CreateDatabase();
        var record = CreateRecord(clock, 1, dataJson);
        await SeedAsync(database, [record]);

        var result = await new AuditAdminService(database).SearchAsync(1, 10, null, CancellationToken.None);

        Assert.Equal(1L, result.TotalCount);
        var item = Assert.Single(result.Items);
        AssertAuditItem(record, item);
        Assert.Equal(length > 220 ? dataJson[..220] + "…" : dataJson, item.Summary);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Audit_search_handles_an_empty_database_without_tracking()
    {
        await using var database = CreateDatabase();

        var result = await new AuditAdminService(database).SearchAsync(1, 10, null, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0L, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Audit_search_returns_an_empty_result_when_no_action_matches()
    {
        await using var database = CreateDatabase();
        await SeedAsync(database, [CreateRecord(new TestClock(), 0, "{}", "License.Disabled")]);

        var result = await new AuditAdminService(database).SearchAsync(1, 10, "activate", CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0L, result.TotalCount);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Audit_search_preserves_the_total_for_a_page_beyond_the_last_row_and_ignores_a_blank_filter()
    {
        await using var database = CreateDatabase();
        await SeedAsync(database, [CreateRecord(new TestClock(), 0, "{}")]);

        var result = await new AuditAdminService(database).SearchAsync(2, 10, " \t ", CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(1L, result.TotalCount);
        Assert.Equal(2, result.Page);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-2, -4, 1, 1)]
    [InlineData(1, 101, 1, 100)]
    public async Task Audit_search_clamps_page_and_page_size(int page, int pageSize, int expectedPage, int expectedPageSize)
    {
        await using var database = CreateDatabase();

        var result = await new AuditAdminService(database).SearchAsync(page, pageSize, null, CancellationToken.None);

        Assert.Equal(expectedPage, result.Page);
        Assert.Equal(expectedPageSize, result.PageSize);
        Assert.Empty(result.Items);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Services_present_fields_after_220_characters_without_changing_raw_summary_limits_or_tracking()
    {
        var clock = new TestClock();
        var dataJson = JsonSerializer.Serialize(new
        {
            padding = new string('x', 260),
            Count = 37,
            validity = "Month",
            DurationUnits = 2
        });
        Assert.True(dataJson.IndexOf("\"Count\"", StringComparison.Ordinal) > 220,
            "The useful fields must be beyond both legacy summary limits.");
        var record = new AuditRecord(Guid.NewGuid(), AuditActorType.Administrator, "test-actor-full-json",
            "BATCH.CREATE", "LicenseBatch", "test-batch-full-json", dataJson, null, "192.0.2.10", clock.UtcNow);
        await using var database = CreateDatabase();
        await SeedAsync(database, [record]);

        var snapshot = await new AdminDashboardQuery(database, clock).GetAsync(CancellationToken.None);
        var result = await new AuditAdminService(database).SearchAsync(1, 10, null, CancellationToken.None);

        var recent = Assert.Single(snapshot.RecentAuditEvents);
        var item = Assert.Single(result.Items);
        AssertAuditItem(record, item);
        Assert.True(recent.Summary == dataJson[..160] + "…" && item.Summary == dataJson[..220] + "…",
            "The original raw dashboard/audit summaries must retain their separate 160/220-character contracts.");
        var recentPresentation = Assert.IsType<AuditPresentation>(recent.Presentation);
        var auditPresentation = Assert.IsType<AuditPresentation>(item.Presentation);
        var expected = AuditPresentationFormatter.Format(record.Action, record.EntityType, dataJson,
            record.ActorType, record.ActorId);
        Assert.True(auditPresentation.Description == expected.Description
            && recentPresentation.Description == expected.Description,
            "Both services must format the full JSON after materializing the bounded query.");
        Assert.True(auditPresentation.Description.Contains("37", StringComparison.Ordinal)
            && auditPresentation.Description.Contains("30", StringComparison.Ordinal),
            "Structured batch count and fixed thirty-day month semantics must survive raw-summary truncation.");
        Assert.True(auditPresentation.ActionLabel == recentPresentation.ActionLabel
            && auditPresentation.TargetLabel.Contains("卡密批次", StringComparison.Ordinal)
            && recentPresentation.TargetLabel.Contains("卡密批次", StringComparison.Ordinal),
            "Audit and dashboard services must share action and object-type presentation.");
        Assert.True(recentPresentation.ActorLabel == AuditPresentationFormatter.Format(record.Action, record.EntityType, dataJson).ActorLabel,
            "The dashboard must not invent an actor when its existing projection has none.");
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Actual_dashboard_query_generates_ordered_limited_postgresql_sql_without_json_string_operations()
    {
        var capture = new AuditQueryCapture();
        await using var database = CreateDatabase(capture);

        await new AdminDashboardQuery(database, new TestClock()).GetAsync(CancellationToken.None);

        var expression = Assert.Single(capture.PagedQueries);
        var sql = GeneratePostgreSql(expression, skip: null, take: 10, normalizedAction: null);
        AssertSafePagedSql(sql);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.OrdinalIgnoreCase);
        AssertNoServerStringTruncation(expression);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Actual_audit_search_query_generates_filtered_ordered_paged_postgresql_sql_without_json_string_operations()
    {
        var capture = new AuditQueryCapture();
        await using var database = CreateDatabase(capture);

        await new AuditAdminService(database).SearchAsync(2, 3, "  aCtIvAtE  ", CancellationToken.None);

        var expression = Assert.Single(capture.PagedQueries);
        var sql = GeneratePostgreSql(expression, skip: 3, take: 3, normalizedAction: "ACTIVATE");
        AssertSafePagedSql(sql);
        Assert.Contains("OFFSET", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upper(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ACTIVATE", sql, StringComparison.Ordinal);
        AssertNoServerStringTruncation(expression);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    private static LicensePlatformDbContext CreateDatabase(AuditQueryCapture? capture = null)
    {
        var options = new DbContextOptionsBuilder<LicensePlatformDbContext>()
            .UseInMemoryDatabase($"admin-audit-query-{Guid.NewGuid():N}");
        if (capture is not null)
        {
            // Compilation must reach this capture even if another fixture ran the same query.
            options.EnableServiceProviderCaching(false).AddInterceptors(capture);
        }

        return new LicensePlatformDbContext(options.Options);
    }

    private static async Task SeedAsync(LicensePlatformDbContext database, IEnumerable<AuditRecord> records)
    {
        database.AuditRecords.AddRange(records);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
    }

    private static AuditRecord CreateRecord(TestClock clock, int index, string dataJson, string? action = null) => new(
        Guid.NewGuid(),
        index % 3 == 0 ? AuditActorType.System : AuditActorType.Administrator,
        index % 3 == 0 ? null : $"test-actor-{index}",
        action ?? $"License.Action{index}",
        "LicenseKey",
        $"test-entity-{index}",
        dataJson,
        $"test-trace-{index}",
        index % 4 == 0 ? null : "192.0.2.10",
        clock.UtcNow.AddMinutes(-index));

    private static string JsonOfLength(int length) =>
        length == 2 ? "{}" : "{\"value\":\"" + new string('x', length - 12) + "\"}";

    private static void AssertAuditItem(AuditRecord expected, AuditListItem actual)
    {
        Assert.Equal(expected.OccurredAtUtc, actual.CreatedAt);
        Assert.Equal(expected.ActorId ?? expected.ActorType.ToString(), actual.Actor);
        Assert.Equal(expected.Action, actual.Action);
        Assert.Equal(expected.EntityType, actual.TargetType);
        Assert.Equal(expected.EntityId, actual.TargetId);
        Assert.Equal("RECORDED", actual.Result);
        Assert.Equal(expected.IpAddress ?? "-", actual.SourceIp);
        Assert.Equal(expected.DataJson.Length > 220 ? expected.DataJson[..220] + "…" : expected.DataJson, actual.Summary);
    }

    private static string GeneratePostgreSql(Expression captured, int? skip, int take, string? normalizedAction)
    {
        // Translation only: this fictional connection is never opened, and has no password.
        var options = new DbContextOptionsBuilder<LicensePlatformDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=admin_audit_sql_only;Username=sql_only")
            .Options;
        using var database = new LicensePlatformDbContext(options);
        Assert.Equal("jsonb", database.Model.FindEntityType(typeof(AuditRecord))!
            .FindProperty(nameof(AuditRecord.DataJson))!.GetColumnType());
        var query = (IQueryable<AuditRecord>)database.AuditRecords;
        // Keep the production Where/OrderBy/Skip/Take/Select, changing only the provider root
        // and binding scalar parameters extracted before QueryCompilationStarting is invoked.
        var expression = new PostgreSqlQueryRebinder(query.Expression, skip, take, normalizedAction).Visit(captured)!;
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        var sql = query.Provider.CreateQuery(expression).ToQueryString();
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        return sql;
    }

    private static void AssertSafePagedSql(string sql)
    {
        Assert.Contains("audit_records", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data_json", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("occurred_at_utc DESC", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("substring", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'Length'", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("->>", sql, StringComparison.Ordinal);
    }

    private static void AssertNoServerStringTruncation(Expression expression)
    {
        var inspection = new AuditQueryInspection();
        inspection.Visit(expression);
        Assert.False(inspection.HasStringLengthOrSubstring,
            "The actual production IQueryable must project raw JSON and truncate only after materialization.");
        Assert.False(inspection.HasPresentationFormattingOrJsonParsing,
            "Presentation formatting and JSON parsing must remain outside the actual production IQueryable.");
    }

    private sealed class AuditQueryCapture : IQueryExpressionInterceptor
    {
        public List<Expression> PagedQueries { get; } = [];

        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            var inspection = new AuditQueryInspection();
            inspection.Visit(queryExpression);
            if (inspection.HasAuditRoot && inspection.HasTake)
            {
                PagedQueries.Add(queryExpression);
            }

            return queryExpression;
        }
    }

    private sealed class AuditQueryInspection : ExpressionVisitor
    {
        public bool HasAuditRoot { get; private set; }
        public bool HasTake { get; private set; }
        public bool HasStringLengthOrSubstring { get; private set; }
        public bool HasPresentationFormattingOrJsonParsing { get; private set; }

        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root && root.EntityType.ClrType == typeof(AuditRecord))
            {
                HasAuditRoot = true;
            }

            return base.VisitExtension(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable) && node.Method.Name == nameof(Queryable.Take))
            {
                HasTake = true;
            }

            if (node.Method.DeclaringType == typeof(string) && node.Method.Name == nameof(string.Substring))
            {
                HasStringLengthOrSubstring = true;
            }

            if (node.Method.DeclaringType == typeof(AuditPresentationFormatter)
                || node.Method.DeclaringType == typeof(JsonDocument))
            {
                HasPresentationFormattingOrJsonParsing = true;
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.DeclaringType == typeof(string) && node.Member.Name == nameof(string.Length))
            {
                HasStringLengthOrSubstring = true;
            }

            return base.VisitMember(node);
        }
    }

    private sealed class PostgreSqlQueryRebinder(
        Expression auditRoot,
        int? skip,
        int take,
        string? normalizedAction) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root && root.EntityType.ClrType == typeof(AuditRecord))
            {
                return auditRoot;
            }

            if (node is QueryParameterExpression parameter)
            {
                if (parameter.Type == typeof(string) && normalizedAction is not null)
                {
                    return Expression.Constant(normalizedAction);
                }

                throw new InvalidOperationException("Unexpected unbound parameter in the captured production audit query.");
            }

            return base.VisitExtension(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable)
                && node.Arguments.Count == 2
                && node.Arguments[1] is QueryParameterExpression)
            {
                if (node.Method.Name == nameof(Queryable.Take))
                {
                    return node.Update(node.Object, [Visit(node.Arguments[0]), Expression.Constant(take)]);
                }

                if (node.Method.Name == nameof(Queryable.Skip) && skip.HasValue)
                {
                    return node.Update(node.Object, [Visit(node.Arguments[0]), Expression.Constant(skip.Value)]);
                }
            }

            return base.VisitMethodCall(node);
        }
    }
}
