using LicensePlatform.Domain.Administration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

public sealed class AdministratorConfiguration : IEntityTypeConfiguration<Administrator>
{
    public void Configure(EntityTypeBuilder<Administrator> builder)
    {
        builder.ToTable("administrators", table =>
        {
            table.HasCheckConstraint("ck_administrators_singleton", "\"SingletonKey\" = 1");
            table.HasCheckConstraint("ck_administrators_failed_access", "\"FailedAccessCount\" >= 0");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Version).IsConcurrencyToken();
        builder.Property(item => item.UserName).HasMaxLength(100).IsRequired();
        builder.Property(item => item.NormalizedUserName).HasMaxLength(100).IsRequired();
        builder.Property(item => item.PasswordHash).HasMaxLength(2_048).IsRequired();
        builder.Property(item => item.FailedAccessCount).IsRequired();
        builder.Property<int>("SingletonKey").HasDefaultValue(1).IsRequired();
        builder.HasIndex("SingletonKey").IsUnique();
        builder.HasIndex(item => item.NormalizedUserName).IsUnique();
    }
}
