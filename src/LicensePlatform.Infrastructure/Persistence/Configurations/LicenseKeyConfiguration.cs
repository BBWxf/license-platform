using LicensePlatform.Domain.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

internal sealed class LicenseKeyConfiguration : IEntityTypeConfiguration<LicenseKey>
{
    public void Configure(EntityTypeBuilder<LicenseKey> builder)
    {
        builder.ToTable("license_keys", table =>
        {
            table.HasCheckConstraint("ck_license_keys_key_hash", "octet_length(\"key_hash\") = 32");
            table.HasCheckConstraint("ck_license_keys_additional_days", "\"additional_days\" >= 0");
            table.HasCheckConstraint("ck_license_keys_binding_version", "\"binding_version\" >= 1");
            table.HasCheckConstraint(
                "ck_license_keys_term",
                "(\"term_unit\" = 'Permanent' AND \"term_value\" = 0) OR (\"term_unit\" <> 'Permanent' AND \"term_value\" > 0)");
            table.HasCheckConstraint(
                "ck_license_keys_activation_expiration",
                "\"activated_at_utc\" IS NOT NULL OR \"expires_at_utc\" IS NULL");
        });
        builder.HasKey(license => license.Id).HasName("pk_license_keys");
        builder.HasAlternateKey(license => new { license.Id, license.ProductId })
            .HasName("ak_license_keys_id_product");
        builder.Property(license => license.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(license => license.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(license => license.BatchId).HasColumnName("batch_id").IsRequired();
        builder.Property(license => license.KeyHash).HasColumnName("key_hash").IsRequired();
        builder.Property(license => license.KeyHint).HasColumnName("key_hint").HasMaxLength(32).IsRequired();
        builder.Property(license => license.TermUnit).HasColumnName("term_unit").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(license => license.TermValue).HasColumnName("term_value").IsRequired();
        builder.Property(license => license.AdditionalDays).HasColumnName("additional_days").IsRequired();
        builder.Property(license => license.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(license => license.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(license => license.ActivatedAtUtc).HasColumnName("activated_at_utc");
        builder.Property(license => license.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(license => license.DisabledAtUtc).HasColumnName("disabled_at_utc");
        builder.Property(license => license.DisabledReason).HasColumnName("disabled_reason").HasMaxLength(500);
        builder.Property(license => license.BindingVersion).HasColumnName("binding_version").IsRequired();
        builder.Property(license => license.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(license => license.KeyHash).IsUnique().HasDatabaseName("ux_license_keys_key_hash");
        builder.HasIndex(license => new { license.ProductId, license.Status, license.ExpiresAtUtc })
            .HasDatabaseName("ix_license_keys_product_status_expiration");
        builder.HasIndex(license => new { license.BatchId, license.CreatedAtUtc })
            .HasDatabaseName("ix_license_keys_batch_created");
        builder.HasOne(license => license.Product)
            .WithMany()
            .HasForeignKey(license => license.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_license_keys_products");
        builder.HasMany(license => license.DeviceBindings)
            .WithOne(binding => binding.LicenseKey)
            .HasForeignKey(binding => binding.LicenseKeyId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_device_bindings_license_keys");
    }
}
