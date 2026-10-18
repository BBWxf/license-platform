using LicensePlatform.Domain.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

internal sealed class ActivationChallengeConfiguration : IEntityTypeConfiguration<ActivationChallenge>
{
    public void Configure(EntityTypeBuilder<ActivationChallenge> builder)
    {
        builder.ToTable("activation_challenges", table =>
        {
            table.HasCheckConstraint("ck_activation_challenges_hash", "octet_length(\"challenge_hash\") = 32");
            table.HasCheckConstraint("ck_activation_challenges_machine_guid_hash", "octet_length(\"machine_guid_hash\") = 32");
            table.HasCheckConstraint("ck_activation_challenges_smbios_hash", "octet_length(\"smbios_hash\") = 32");
            table.HasCheckConstraint("ck_activation_challenges_system_volume_hash", "octet_length(\"system_volume_hash\") = 32");
            table.HasCheckConstraint("ck_activation_challenges_nonce", "octet_length(\"nonce\") = 32");
            table.HasCheckConstraint("ck_activation_challenges_binding_version", "\"binding_version\" >= 1");
            table.HasCheckConstraint("ck_activation_challenges_expiration", "\"expires_at_utc\" > \"created_at_utc\"");
        });
        builder.HasKey(challenge => challenge.Id).HasName("pk_activation_challenges");
        builder.Property(challenge => challenge.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(challenge => challenge.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(challenge => challenge.LicenseKeyId).HasColumnName("license_key_id").IsRequired();
        builder.Property(challenge => challenge.ChallengeHash).HasColumnName("challenge_hash").IsRequired();
        builder.Property(challenge => challenge.MachineGuidHash).HasColumnName("machine_guid_hash").IsRequired();
        builder.Property(challenge => challenge.SmbiosHash).HasColumnName("smbios_hash").IsRequired();
        builder.Property(challenge => challenge.SystemVolumeHash).HasColumnName("system_volume_hash").IsRequired();
        builder.Property(challenge => challenge.Nonce).HasColumnName("nonce").IsRequired();
        builder.Property(challenge => challenge.DeviceHint).HasColumnName("device_hint").HasMaxLength(128).IsRequired();
        builder.Property(challenge => challenge.DevicePublicKey).HasColumnName("device_public_key").HasMaxLength(2_048).IsRequired();
        builder.Property(challenge => challenge.Purpose).HasColumnName("purpose").HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(challenge => challenge.BindingVersion).HasColumnName("binding_version").IsRequired();
        builder.Property(challenge => challenge.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(challenge => challenge.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(challenge => challenge.ConsumedAtUtc).HasColumnName("consumed_at_utc");
        builder.Property(challenge => challenge.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(challenge => challenge.ChallengeHash)
            .IsUnique()
            .HasDatabaseName("ux_activation_challenges_hash");
        builder.HasIndex(challenge => new { challenge.LicenseKeyId, challenge.ConsumedAtUtc, challenge.ExpiresAtUtc })
            .HasDatabaseName("ix_activation_challenges_license_availability");
        builder.HasOne(challenge => challenge.Product)
            .WithMany()
            .HasForeignKey(challenge => challenge.ProductId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_activation_challenges_products");
        builder.HasOne(challenge => challenge.LicenseKey)
            .WithMany()
            .HasForeignKey(challenge => new { challenge.LicenseKeyId, challenge.ProductId })
            .HasPrincipalKey(license => new { license.Id, license.ProductId })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_activation_challenges_license_keys");
    }
}
