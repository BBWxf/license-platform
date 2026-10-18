using LicensePlatform.Domain.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

internal sealed class ProductSigningKeyConfiguration : IEntityTypeConfiguration<ProductSigningKey>
{
    public void Configure(EntityTypeBuilder<ProductSigningKey> builder)
    {
        builder.ToTable("product_signing_keys", table =>
        {
            table.HasCheckConstraint("ck_product_signing_keys_algorithm", "\"algorithm\" = 'ES256'");
            table.HasCheckConstraint("ck_product_signing_keys_private_key", "octet_length(\"protected_private_key\") > 0");
        });
        builder.HasKey(key => key.Id).HasName("pk_product_signing_keys");
        builder.Property(key => key.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(key => key.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(key => key.KeyId).HasColumnName("key_id").HasMaxLength(128).IsRequired();
        builder.Property(key => key.Algorithm).HasColumnName("algorithm").HasMaxLength(16).IsRequired();
        builder.Property(key => key.PublicKeyPem).HasColumnName("public_key_pem").HasMaxLength(2_048).IsRequired();
        builder.Property(key => key.ProtectedPrivateKey).HasColumnName("protected_private_key").IsRequired();
        builder.Property(key => key.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(key => key.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(key => key.RetiredAtUtc).HasColumnName("retired_at_utc");
        builder.Property(key => key.RevokedAtUtc).HasColumnName("revoked_at_utc");
        builder.Property(key => key.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(key => new { key.ProductId, key.KeyId })
            .IsUnique()
            .HasDatabaseName("ux_product_signing_keys_product_key_id");
        builder.HasIndex(key => key.ProductId)
            .IsUnique()
            .HasFilter("\"status\" = 'Active'")
            .HasDatabaseName("ux_product_signing_keys_one_active");
    }
}
