using LicensePlatform.Domain.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

internal sealed class LicenseBatchConfiguration : IEntityTypeConfiguration<LicenseBatch>
{
    public void Configure(EntityTypeBuilder<LicenseBatch> builder)
    {
        builder.ToTable("license_batches", table =>
        {
            table.HasCheckConstraint("ck_license_batches_quantity", "\"quantity\" BETWEEN 1 AND 1000000");
            table.HasCheckConstraint("ck_license_batches_issued_count", "\"issued_count\" BETWEEN 0 AND \"quantity\"");
            table.HasCheckConstraint(
                "ck_license_batches_term",
                "(\"term_unit\" = 'Permanent' AND \"term_value\" = 0) OR (\"term_unit\" <> 'Permanent' AND \"term_value\" > 0)");
        });
        builder.HasKey(batch => batch.Id).HasName("pk_license_batches");
        builder.HasAlternateKey(batch => new { batch.Id, batch.ProductId })
            .HasName("ak_license_batches_id_product");
        builder.Property(batch => batch.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(batch => batch.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(batch => batch.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(batch => batch.Quantity).HasColumnName("quantity").IsRequired();
        builder.Property(batch => batch.IssuedCount).HasColumnName("issued_count").IsRequired();
        builder.Property(batch => batch.TermUnit).HasColumnName("term_unit").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(batch => batch.TermValue).HasColumnName("term_value").IsRequired();
        builder.Property(batch => batch.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(batch => batch.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(batch => batch.IssuedAtUtc).HasColumnName("issued_at_utc");
        builder.Property(batch => batch.CancelledAtUtc).HasColumnName("cancelled_at_utc");
        builder.Property(batch => batch.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(batch => new { batch.ProductId, batch.CreatedAtUtc })
            .HasDatabaseName("ix_license_batches_product_created");
        builder.HasOne(batch => batch.Product)
            .WithMany()
            .HasForeignKey(batch => batch.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_license_batches_products");
        builder.HasMany(batch => batch.LicenseKeys)
            .WithOne(key => key.Batch)
            .HasForeignKey(key => new { key.BatchId, key.ProductId })
            .HasPrincipalKey(batch => new { batch.Id, batch.ProductId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_license_keys_batches");
    }
}
