using LicensePlatform.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationSettingConfiguration : IEntityTypeConfiguration<ApplicationSetting>
{
    public void Configure(EntityTypeBuilder<ApplicationSetting> builder)
    {
        builder.ToTable("application_settings");
        builder.HasKey(setting => setting.Id).HasName("pk_application_settings");
        builder.Property(setting => setting.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(setting => setting.Key).HasColumnName("key").HasMaxLength(200).IsRequired();
        builder.Property(setting => setting.ValueJson).HasColumnName("value_json").HasColumnType("jsonb").IsRequired();
        builder.Property(setting => setting.IsSecret).HasColumnName("is_secret").IsRequired();
        builder.Property(setting => setting.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.Property(setting => setting.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(setting => setting.Key).IsUnique().HasDatabaseName("ux_application_settings_key");
        builder.HasIndex(setting => setting.UpdatedAtUtc).HasDatabaseName("ix_application_settings_updated");
    }
}
