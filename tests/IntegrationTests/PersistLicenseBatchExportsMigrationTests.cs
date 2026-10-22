using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace LicensePlatform.IntegrationTests;

public sealed class PersistLicenseBatchExportsMigrationTests
{
    private const string InitialMigration = "20260911151418_InitialCreate";
    private const string DurableMigration = "20261022000000_PersistLicenseBatchExports";
    private const string ExportTable = "license_batch_exports";
    private const string ExpirationConstraint = "ck_license_batch_exports_expiration";
    private const string DurableExpirationCheck = "\"expires_at_utc\" IS NULL OR \"expires_at_utc\" > \"created_at_utc\"";
    private const string FiniteExpirationCheck = "\"expires_at_utc\" > \"created_at_utc\"";
    private const string PromotionSql = """
        UPDATE "license_batch_exports"
        SET "expires_at_utc" = NULL,
            "version" = "version" + 1
        WHERE octet_length("protected_payload") > 0
          AND "expires_at_utc" IS NOT NULL;
        """;
    private const string RollbackGuardSql = """
        DO $license_batch_exports_down$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM "license_batch_exports"
                WHERE "expires_at_utc" IS NULL
            ) THEN
                RAISE EXCEPTION 'Cannot revert durable license batch exports: null expirations exist.';
            END IF;
        END;
        $license_batch_exports_down$;
        """;

    [Fact]
    public void Up_OnlyPromotesNonemptyFinitePayloadsWithoutRewritingRetainedData()
    {
        var operations = new PersistLicenseBatchExports().UpOperations;

        Assert.Collection(
            operations,
            operation =>
            {
                var drop = Assert.IsType<DropCheckConstraintOperation>(operation);
                Assert.Equal(ExportTable, drop.Table);
                Assert.Equal(ExpirationConstraint, drop.Name);
            },
            operation =>
            {
                var alter = Assert.IsType<AlterColumnOperation>(operation);
                Assert.Equal(ExportTable, alter.Table);
                Assert.Equal("expires_at_utc", alter.Name);
                Assert.Equal("timestamp with time zone", alter.ColumnType);
                Assert.True(alter.IsNullable);
                Assert.False(alter.OldColumn.IsNullable);
                Assert.Null(alter.DefaultValue);
                Assert.Null(alter.DefaultValueSql);
            },
            operation =>
            {
                var add = Assert.IsType<AddCheckConstraintOperation>(operation);
                Assert.Equal(ExportTable, add.Table);
                Assert.Equal(ExpirationConstraint, add.Name);
                Assert.Equal(DurableExpirationCheck, add.Sql);
            },
            operation =>
            {
                var promotion = Assert.IsType<SqlOperation>(operation);
                // The complete UPDATE changes only expiration and concurrency version.
                // There is deliberately no consumption/current-time filter, so consumed or
                // expired retained payloads are promoted, but empty/already-durable rows are not.
                Assert.Equal(PromotionSql, promotion.Sql);
                Assert.False(promotion.SuppressTransaction);
            });
    }

    [Fact]
    public void Down_RejectsEveryNullExpirationBeforeRestoringTheOriginalSchema()
    {
        var operations = new PersistLicenseBatchExports().DownOperations;

        Assert.Collection(
            operations,
            operation =>
            {
                var guard = Assert.IsType<SqlOperation>(operation);
                // This covers even empty or historically consumed durable rows.
                Assert.Equal(RollbackGuardSql, guard.Sql);
                Assert.False(guard.SuppressTransaction);
            },
            operation =>
            {
                var drop = Assert.IsType<DropCheckConstraintOperation>(operation);
                Assert.Equal(ExportTable, drop.Table);
                Assert.Equal(ExpirationConstraint, drop.Name);
            },
            operation =>
            {
                var alter = Assert.IsType<AlterColumnOperation>(operation);
                Assert.Equal(ExportTable, alter.Table);
                Assert.Equal("expires_at_utc", alter.Name);
                Assert.Equal("timestamp with time zone", alter.ColumnType);
                Assert.False(alter.IsNullable);
                Assert.True(alter.OldColumn.IsNullable);
                Assert.Null(alter.DefaultValue);
                Assert.Null(alter.DefaultValueSql);
            },
            operation =>
            {
                var add = Assert.IsType<AddCheckConstraintOperation>(operation);
                Assert.Equal(ExportTable, add.Table);
                Assert.Equal(ExpirationConstraint, add.Name);
                Assert.Equal(FiniteExpirationCheck, add.Sql);
            });
    }

    [Fact]
    public void IdempotentUpScript_ProtectsThePromotionWithMigrationHistory()
    {
        using var context = CreateContext();

        var script = context.GetService<IMigrator>().GenerateScript(
            InitialMigration,
            DurableMigration,
            MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains(DurableMigration, context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Contains("ALTER TABLE license_batch_exports ALTER COLUMN expires_at_utc DROP NOT NULL;", script);
        Assert.Contains($"ADD CONSTRAINT {ExpirationConstraint} CHECK ({DurableExpirationCheck})", script);
        Assert.Contains(NormalizeSql(PromotionSql), NormalizeSql(script));
        Assert.Matches(
            "IF NOT EXISTS\\s*\\(SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '"
            + DurableMigration + "'\\) THEN\\s*UPDATE \"license_batch_exports\"",
            script);
        Assert.True(
            script.IndexOf("DROP NOT NULL", StringComparison.Ordinal)
            < script.IndexOf("UPDATE \"license_batch_exports\"", StringComparison.Ordinal));
        Assert.DoesNotContain("DELETE FROM", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("TRUNCATE", script);
    }

    [Fact]
    public void DownScript_RaisesBeforeChangingNullabilityAndNeverInventsOrDeletesData()
    {
        using var context = CreateContext();

        var script = context.GetService<IMigrator>().GenerateScript(DurableMigration, InitialMigration);

        Assert.Contains(NormalizeSql(RollbackGuardSql), NormalizeSql(script));
        Assert.Contains("ALTER TABLE license_batch_exports ALTER COLUMN expires_at_utc SET NOT NULL;", script);
        Assert.Contains($"ADD CONSTRAINT {ExpirationConstraint} CHECK ({FiniteExpirationCheck})", script);
        Assert.True(
            script.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal)
            < script.IndexOf("DROP CONSTRAINT", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal)
            < script.IndexOf("SET NOT NULL", StringComparison.Ordinal));
        Assert.DoesNotContain("UPDATE ", script);
        Assert.DoesNotContain("DELETE FROM license_batch_exports", script);
        Assert.DoesNotContain("DELETE FROM \"license_batch_exports\"", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("TRUNCATE", script);
        Assert.DoesNotContain("SET DEFAULT", script);
        Assert.DoesNotContain("COALESCE", script);
    }

    private static string NormalizeSql(string sql)
    {
        return string.Join("\n", sql.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    }

    private static LicensePlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LicensePlatformDbContext>()
            .UseNpgsql("Host=localhost;Database=license_platform_model_tests;Username=unused;Password=unused")
            .Options;
        return new LicensePlatformDbContext(options);
    }
}
