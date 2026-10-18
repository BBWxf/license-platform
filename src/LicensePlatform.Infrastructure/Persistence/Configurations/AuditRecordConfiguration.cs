using LicensePlatform.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensePlatform.Infrastructure.Persistence.Configurations;

internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("audit_records");
        builder.HasKey(record => record.Id).HasName("pk_audit_records");
        builder.Property(record => record.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(record => record.ActorType).HasColumnName("actor_type").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(record => record.ActorId).HasColumnName("actor_id").HasMaxLength(200);
        builder.Property(record => record.Action).HasColumnName("action").HasMaxLength(128).IsRequired();
        builder.Property(record => record.EntityType).HasColumnName("entity_type").HasMaxLength(128).IsRequired();
        builder.Property(record => record.EntityId).HasColumnName("entity_id").HasMaxLength(200).IsRequired();
        builder.Property(record => record.DataJson).HasColumnName("data_json").HasColumnType("jsonb").IsRequired();
        builder.Property(record => record.TraceId).HasColumnName("trace_id").HasMaxLength(128);
        builder.Property(record => record.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
        builder.Property(record => record.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(record => record.Version).HasColumnName("version").ValueGeneratedNever();

        builder.HasIndex(record => record.OccurredAtUtc).HasDatabaseName("ix_audit_records_occurred");
        builder.HasIndex(record => new { record.EntityType, record.EntityId, record.OccurredAtUtc })
            .HasDatabaseName("ix_audit_records_entity");
        builder.HasIndex(record => new { record.Action, record.OccurredAtUtc })
            .HasDatabaseName("ix_audit_records_action");
        builder.HasIndex(record => record.TraceId).HasDatabaseName("ix_audit_records_trace_id");
    }
}
