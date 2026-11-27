using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class AttemptEventConfiguration : IEntityTypeConfiguration<AttemptEvent>
{
    public void Configure(EntityTypeBuilder<AttemptEvent> builder)
    {
        // Append-only event: no concurrency token or update-specific assumptions.
        builder.ToTable("attempt_event");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.AttemptId).HasColumnName("attempt_id");
        builder.Property(x => x.ActorId).HasColumnName("actor_id");
        builder.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.ClientAt).HasColumnName("client_at").HasColumnType("timestamptz");
        builder.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasColumnName("user_agent");
        builder.Property(x => x.EventKey).HasColumnName("event_key").HasMaxLength(128).IsRequired();
        builder.HasOne<ExamAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.EventKey).IsUnique().HasDatabaseName("ux_attempt_event_event_key");
        builder.HasIndex(x => new { x.AttemptId, x.OccurredAt, x.Id })
            .HasDatabaseName("ix_attempt_event_attempt_occurred").IsDescending(false, true, true);
        builder.HasIndex(x => x.ActorId).HasDatabaseName("ix_attempt_event_actor_id");
        builder.HasIndex(x => new { x.EventType, x.OccurredAt, x.Id })
            .HasDatabaseName("ix_attempt_event_type_occurred").IsDescending(false, true, true);
    }
}
