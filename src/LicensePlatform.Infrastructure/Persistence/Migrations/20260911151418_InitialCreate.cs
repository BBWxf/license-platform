using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicensePlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "administrators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedUserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    FailedAccessCount = table.Column<int>(type: "integer", nullable: false),
                    LockoutEndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PasswordChangedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SingletonKey = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrators", x => x.Id);
                    table.CheckConstraint("ck_administrators_failed_access", "\"FailedAccessCount\" >= 0");
                    table.CheckConstraint("ck_administrators_singleton", "\"SingletonKey\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "application_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value_json = table.Column<string>(type: "jsonb", nullable: false),
                    is_secret = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    data_json = table.Column<string>(type: "jsonb", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    license_prefix = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    lease_lifetime_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_lease_lifetime", "\"lease_lifetime_minutes\" BETWEEN 5 AND 43200");
                    table.CheckConstraint("ck_products_license_prefix", "\"license_prefix\" ~ '^[A-Z0-9]{2,12}$'");
                });

            migrationBuilder.CreateTable(
                name: "license_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    issued_count = table.Column<int>(type: "integer", nullable: false),
                    term_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    term_value = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_license_batches", x => x.id);
                    table.UniqueConstraint("ak_license_batches_id_product", x => new { x.id, x.product_id });
                    table.CheckConstraint("ck_license_batches_issued_count", "\"issued_count\" BETWEEN 0 AND \"quantity\"");
                    table.CheckConstraint("ck_license_batches_quantity", "\"quantity\" BETWEEN 1 AND 1000000");
                    table.CheckConstraint("ck_license_batches_term", "(\"term_unit\" = 'Permanent' AND \"term_value\" = 0) OR (\"term_unit\" <> 'Permanent' AND \"term_value\" > 0)");
                    table.ForeignKey(
                        name: "fk_license_batches_products",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_signing_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    algorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    public_key_pem = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    protected_private_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_signing_keys", x => x.id);
                    table.CheckConstraint("ck_product_signing_keys_algorithm", "\"algorithm\" = 'ES256'");
                    table.CheckConstraint("ck_product_signing_keys_private_key", "octet_length(\"protected_private_key\") > 0");
                    table.ForeignKey(
                        name: "fk_product_signing_keys_products",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "license_batch_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    download_token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    protected_payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_license_batch_exports", x => x.id);
                    table.CheckConstraint("ck_license_batch_exports_expiration", "\"expires_at_utc\" > \"created_at_utc\"");
                    table.CheckConstraint("ck_license_batch_exports_token_hash", "octet_length(\"download_token_hash\") = 32");
                    table.ForeignKey(
                        name: "fk_license_batch_exports_batches",
                        column: x => x.batch_id,
                        principalTable: "license_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "license_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_hint = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    term_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    term_value = table.Column<int>(type: "integer", nullable: false),
                    additional_days = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    binding_version = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_license_keys", x => x.id);
                    table.UniqueConstraint("ak_license_keys_id_product", x => new { x.id, x.product_id });
                    table.CheckConstraint("ck_license_keys_activation_expiration", "\"activated_at_utc\" IS NOT NULL OR \"expires_at_utc\" IS NULL");
                    table.CheckConstraint("ck_license_keys_additional_days", "\"additional_days\" >= 0");
                    table.CheckConstraint("ck_license_keys_binding_version", "\"binding_version\" >= 1");
                    table.CheckConstraint("ck_license_keys_key_hash", "octet_length(\"key_hash\") = 32");
                    table.CheckConstraint("ck_license_keys_term", "(\"term_unit\" = 'Permanent' AND \"term_value\" = 0) OR (\"term_unit\" <> 'Permanent' AND \"term_value\" > 0)");
                    table.ForeignKey(
                        name: "fk_license_keys_batches",
                        columns: x => new { x.batch_id, x.product_id },
                        principalTable: "license_batches",
                        principalColumns: new[] { "id", "product_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_license_keys_products",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "activation_challenges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    machine_guid_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    smbios_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    system_volume_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    device_hint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    device_public_key = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    purpose = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    binding_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activation_challenges", x => x.id);
                    table.CheckConstraint("ck_activation_challenges_binding_version", "\"binding_version\" >= 1");
                    table.CheckConstraint("ck_activation_challenges_expiration", "\"expires_at_utc\" > \"created_at_utc\"");
                    table.CheckConstraint("ck_activation_challenges_hash", "octet_length(\"challenge_hash\") = 32");
                    table.CheckConstraint("ck_activation_challenges_machine_guid_hash", "octet_length(\"machine_guid_hash\") = 32");
                    table.CheckConstraint("ck_activation_challenges_nonce", "octet_length(\"nonce\") = 32");
                    table.CheckConstraint("ck_activation_challenges_smbios_hash", "octet_length(\"smbios_hash\") = 32");
                    table.CheckConstraint("ck_activation_challenges_system_volume_hash", "octet_length(\"system_volume_hash\") = 32");
                    table.ForeignKey(
                        name: "fk_activation_challenges_license_keys",
                        columns: x => new { x.license_key_id, x.product_id },
                        principalTable: "license_keys",
                        principalColumns: new[] { "id", "product_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_activation_challenges_products",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_bindings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_guid_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    smbios_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    system_volume_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    device_hint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    device_public_key = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    binding_version = table.Column<long>(type: "bigint", nullable: false),
                    bound_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    unbound_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_bindings", x => x.id);
                    table.CheckConstraint("ck_device_bindings_binding_version", "\"binding_version\" >= 1");
                    table.CheckConstraint("ck_device_bindings_machine_guid_hash", "octet_length(\"machine_guid_hash\") = 32");
                    table.CheckConstraint("ck_device_bindings_smbios_hash", "octet_length(\"smbios_hash\") = 32");
                    table.CheckConstraint("ck_device_bindings_system_volume_hash", "octet_length(\"system_volume_hash\") = 32");
                    table.CheckConstraint("ck_device_bindings_unbound_after_bound", "\"unbound_at_utc\" IS NULL OR \"unbound_at_utc\" >= \"bound_at_utc\"");
                    table.ForeignKey(
                        name: "fk_device_bindings_license_keys",
                        column: x => x.license_key_id,
                        principalTable: "license_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_activation_challenges_license_availability",
                table: "activation_challenges",
                columns: new[] { "license_key_id", "consumed_at_utc", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_activation_challenges_license_key_id_product_id",
                table: "activation_challenges",
                columns: new[] { "license_key_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "IX_activation_challenges_product_id",
                table: "activation_challenges",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_activation_challenges_hash",
                table: "activation_challenges",
                column: "challenge_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_administrators_NormalizedUserName",
                table: "administrators",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_administrators_SingletonKey",
                table: "administrators",
                column: "SingletonKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_settings_updated",
                table: "application_settings",
                column: "updated_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_application_settings_key",
                table: "application_settings",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_records_action",
                table: "audit_records",
                columns: new[] { "action", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_records_entity",
                table: "audit_records",
                columns: new[] { "entity_type", "entity_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_records_occurred",
                table: "audit_records",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_audit_records_trace_id",
                table: "audit_records",
                column: "trace_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_bindings_machine_guid_hash",
                table: "device_bindings",
                column: "machine_guid_hash");

            migrationBuilder.CreateIndex(
                name: "ix_device_bindings_smbios_hash",
                table: "device_bindings",
                column: "smbios_hash");

            migrationBuilder.CreateIndex(
                name: "ix_device_bindings_system_volume_hash",
                table: "device_bindings",
                column: "system_volume_hash");

            migrationBuilder.CreateIndex(
                name: "ux_device_bindings_license_version",
                table: "device_bindings",
                columns: new[] { "license_key_id", "binding_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_device_bindings_one_active_per_license",
                table: "device_bindings",
                column: "license_key_id",
                unique: true,
                filter: "\"unbound_at_utc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_license_batch_exports_availability",
                table: "license_batch_exports",
                columns: new[] { "consumed_at_utc", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_license_batch_exports_batch",
                table: "license_batch_exports",
                column: "batch_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_license_batch_exports_token_hash",
                table: "license_batch_exports",
                column: "download_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_license_batches_product_created",
                table: "license_batches",
                columns: new[] { "product_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_license_keys_batch_created",
                table: "license_keys",
                columns: new[] { "batch_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_license_keys_batch_id_product_id",
                table: "license_keys",
                columns: new[] { "batch_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_license_keys_product_status_expiration",
                table: "license_keys",
                columns: new[] { "product_id", "status", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_license_keys_key_hash",
                table: "license_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_signing_keys_one_active",
                table: "product_signing_keys",
                column: "product_id",
                unique: true,
                filter: "\"status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_product_signing_keys_product_key_id",
                table: "product_signing_keys",
                columns: new[] { "product_id", "key_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_enabled_name",
                table: "products",
                columns: new[] { "is_enabled", "name" });

            migrationBuilder.CreateIndex(
                name: "ux_products_code",
                table: "products",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_products_license_prefix",
                table: "products",
                column: "license_prefix",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activation_challenges");

            migrationBuilder.DropTable(
                name: "administrators");

            migrationBuilder.DropTable(
                name: "application_settings");

            migrationBuilder.DropTable(
                name: "audit_records");

            migrationBuilder.DropTable(
                name: "device_bindings");

            migrationBuilder.DropTable(
                name: "license_batch_exports");

            migrationBuilder.DropTable(
                name: "product_signing_keys");

            migrationBuilder.DropTable(
                name: "license_keys");

            migrationBuilder.DropTable(
                name: "license_batches");

            migrationBuilder.DropTable(
                name: "products");
        }
    }
}
