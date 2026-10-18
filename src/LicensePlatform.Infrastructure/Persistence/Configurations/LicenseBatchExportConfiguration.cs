using LicensePlatform.Domain.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

internal sealed class LicenseBatchExportConfiguration : IEntityTypeConfiguration<LicenseBatchExport>
{
    public void Configure(EntityTypeBuilder<LicenseBatchExport> builder)
    {
        builder.ToTable("license_batch_exports", table =>
        {
            table.HasCheckConstraint("ck_license_batch_exports_token_hash", "octet_length(\"download_token_hash\") = 32");
            table.HasCheckConstraint("ck_license_batch_exports_expiration", "\"expires_at_utc\" > \"created_at_utc\"");
        });
        builder.HasKey(export => export.Id).HasName("pk_license_batch_exports");
        builder.Property(export => export.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(export => export.BatchId).HasColumnName("batch_id").IsRequired();
        builder.Property(export => export.DownloadTokenHash).HasColumnName("download_token_hash").IsRequired();
        builder.Property(export => export.ProtectedPayload).HasColumnName("protected_payload").IsRequired();
        builder.Property(export => export.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(export => export.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(export => export.ConsumedAtUtc).HasColumnName("consumed_at_utc");
        builder.Property(export => export.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(export => export.BatchId).IsUnique().HasDatabaseName("ux_license_batch_exports_batch");
        builder.HasIndex(export => export.DownloadTokenHash).IsUnique().HasDatabaseName("ux_license_batch_exports_token_hash");
        builder.HasIndex(export => new { export.ConsumedAtUtc, export.ExpiresAtUtc })
            .HasDatabaseName("ix_license_batch_exports_availability");
        builder.HasOne(export => export.Batch)
            .WithOne()
            .HasForeignKey<LicenseBatchExport>(export => export.BatchId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_license_batch_exports_batches");
    }
}
