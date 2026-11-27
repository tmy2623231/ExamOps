using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_message");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.EventKey).HasColumnName("event_key").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").IsRequired();
        builder.Property(x => x.AggregateType).HasColumnName("aggregate_type").IsRequired();
        builder.Property(x => x.AggregateId).HasColumnName("aggregate_id");
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Pending").IsRequired();
        builder.Property(x => x.RetryCount).HasColumnName("retry_count").HasDefaultValue(0);
        builder.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.LockedAt).HasColumnName("locked_at").HasColumnType("timestamptz");
        builder.Property(x => x.LastError).HasColumnName("last_error");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.SentAt).HasColumnName("sent_at").HasColumnType("timestamptz");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasIndex(x => x.EventKey).IsUnique().HasDatabaseName("ux_outbox_event_key");
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt, x.Id }).HasDatabaseName("ix_outbox_pending")
            .HasFilter("status IN ('Pending','Dead')");
        builder.HasIndex(x => new { x.Status, x.LockedAt, x.Id }).HasDatabaseName("ix_outbox_processing")
            .HasFilter("status = 'Processing'");
        builder.HasIndex(x => new { x.AggregateType, x.AggregateId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_outbox_aggregate").IsDescending(false, false, true, true);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_outbox_status_values", "status IN ('Pending','Processing','Sent','Dead')");
            t.HasCheckConstraint("ck_outbox_retry", "retry_count >= 0");
            t.HasCheckConstraint("ck_outbox_sent", "status <> 'Sent' OR sent_at IS NOT NULL");
            t.HasCheckConstraint("ck_outbox_payload", "jsonb_typeof(payload_json) = 'object'");
        });
    }
}
