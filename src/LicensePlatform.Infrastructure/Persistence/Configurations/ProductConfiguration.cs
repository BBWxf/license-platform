using LicensePlatform.Domain.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", table =>
        {
            table.HasCheckConstraint("ck_products_lease_lifetime", "\"lease_lifetime_minutes\" BETWEEN 5 AND 43200");
            table.HasCheckConstraint("ck_products_license_prefix", "\"license_prefix\" ~ '^[A-Z0-9]{2,12}$'");
        });
        builder.HasKey(product => product.Id).HasName("pk_products");
        builder.Property(product => product.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(product => product.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(product => product.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(product => product.IsEnabled).HasColumnName("is_enabled").IsRequired();
        builder.Property(product => product.LicensePrefix).HasColumnName("license_prefix").HasMaxLength(12).IsRequired();
        builder.Property(product => product.LeaseLifetimeMinutes).HasColumnName("lease_lifetime_minutes").IsRequired();
        builder.Property(product => product.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(product => product.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.Property(product => product.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(product => product.Code).IsUnique().HasDatabaseName("ux_products_code");
        builder.HasIndex(product => product.LicensePrefix).IsUnique().HasDatabaseName("ux_products_license_prefix");
        builder.HasIndex(product => new { product.IsEnabled, product.Name }).HasDatabaseName("ix_products_enabled_name");
        builder.HasMany(product => product.SigningKeys)
            .WithOne(key => key.Product)
            .HasForeignKey(key => key.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_product_signing_keys_products");
    }
}
