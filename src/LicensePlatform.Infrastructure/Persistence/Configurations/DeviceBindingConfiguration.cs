using LicensePlatform.Domain.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceBindingConfiguration : IEntityTypeConfiguration<DeviceBinding>
{
    public void Configure(EntityTypeBuilder<DeviceBinding> builder)
    {
        builder.ToTable("device_bindings", table =>
        {
            table.HasCheckConstraint("ck_device_bindings_machine_guid_hash", "octet_length(\"machine_guid_hash\") = 32");
            table.HasCheckConstraint("ck_device_bindings_smbios_hash", "octet_length(\"smbios_hash\") = 32");
            table.HasCheckConstraint("ck_device_bindings_system_volume_hash", "octet_length(\"system_volume_hash\") = 32");
            table.HasCheckConstraint("ck_device_bindings_binding_version", "\"binding_version\" >= 1");
            table.HasCheckConstraint(
                "ck_device_bindings_unbound_after_bound",
                "\"unbound_at_utc\" IS NULL OR \"unbound_at_utc\" >= \"bound_at_utc\"");
        });
        builder.HasKey(binding => binding.Id).HasName("pk_device_bindings");
        builder.Property(binding => binding.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(binding => binding.LicenseKeyId).HasColumnName("license_key_id").IsRequired();
        builder.Property(binding => binding.MachineGuidHash).HasColumnName("machine_guid_hash").IsRequired();
        builder.Property(binding => binding.SmbiosHash).HasColumnName("smbios_hash").IsRequired();
        builder.Property(binding => binding.SystemVolumeHash).HasColumnName("system_volume_hash").IsRequired();
        builder.Property(binding => binding.DeviceHint).HasColumnName("device_hint").HasMaxLength(128).IsRequired();
        builder.Property(binding => binding.DevicePublicKey).HasColumnName("device_public_key").HasMaxLength(2_048).IsRequired();
        builder.Property(binding => binding.BindingVersion).HasColumnName("binding_version").IsRequired();
        builder.Property(binding => binding.BoundAtUtc).HasColumnName("bound_at_utc").IsRequired();
        builder.Property(binding => binding.UnboundAtUtc).HasColumnName("unbound_at_utc");
        builder.Property(binding => binding.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(binding => binding.LicenseKeyId)
            .IsUnique()
            .HasFilter("\"unbound_at_utc\" IS NULL")
            .HasDatabaseName("ux_device_bindings_one_active_per_license");
        builder.HasIndex(binding => new { binding.LicenseKeyId, binding.BindingVersion })
            .IsUnique()
            .HasDatabaseName("ux_device_bindings_license_version");
        builder.HasIndex(binding => binding.MachineGuidHash).HasDatabaseName("ix_device_bindings_machine_guid_hash");
        builder.HasIndex(binding => binding.SmbiosHash).HasDatabaseName("ix_device_bindings_smbios_hash");
        builder.HasIndex(binding => binding.SystemVolumeHash).HasDatabaseName("ix_device_bindings_system_volume_hash");
    }
}
