using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicensePlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistLicenseBatchExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_license_batch_exports_expiration",
                table: "license_batch_exports");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "expires_at_utc",
                table: "license_batch_exports",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddCheckConstraint(
                name: "ck_license_batch_exports_expiration",
                table: "license_batch_exports",
                sql: "\"expires_at_utc\" IS NULL OR \"expires_at_utc\" > \"created_at_utc\"");

            // Retained payloads remain available regardless of legacy expiration or consumption.
            // Do not rewrite their encrypted bytes, token hashes, or consumption history.
            migrationBuilder.Sql(
                """
                UPDATE "license_batch_exports"
                SET "expires_at_utc" = NULL,
                    "version" = "version" + 1
                WHERE octet_length("protected_payload") > 0
                  AND "expires_at_utc" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A rollback cannot represent durable exports without inventing an expiration.
            migrationBuilder.Sql(
                """
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
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_license_batch_exports_expiration",
                table: "license_batch_exports");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "expires_at_utc",
                table: "license_batch_exports",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_license_batch_exports_expiration",
                table: "license_batch_exports",
                sql: "\"expires_at_utc\" > \"created_at_utc\"");
        }
    }
}
