using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_event");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.ActorId).HasColumnName("actor_id");
        builder.Property(x => x.ActorRole).HasColumnName("actor_role");
        builder.Property(x => x.Action).HasColumnName("action").IsRequired();
        builder.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.AfterJson).HasColumnName("after_json").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.RequestId).HasColumnName("request_id");
        builder.Property(x => x.TraceId).HasColumnName("trace_id");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
        builder.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(128);
        builder.Property(x => x.HttpMethod).HasColumnName("http_method").HasMaxLength(16);
        builder.Property(x => x.RouteTemplate).HasColumnName("route_template");
        builder.Property(x => x.ResultRefJson).HasColumnName("result_ref_json").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.EventKey).HasColumnName("event_key").HasMaxLength(128);
        builder.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasColumnName("user_agent");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_audit_entity_created");
        builder.HasIndex(x => new { x.ActorId, x.CreatedAt, x.Id }).HasDatabaseName("ix_audit_actor_created");
        builder.HasIndex(x => new { x.Action, x.CreatedAt, x.Id }).HasDatabaseName("ix_audit_action_created");
        builder.HasIndex(x => new { x.ActorId, x.HttpMethod, x.RouteTemplate, x.IdempotencyKey }).IsUnique()
            .HasDatabaseName("ux_audit_api_idempotency").HasFilter("actor_id IS NOT NULL AND http_method IS NOT NULL AND route_template IS NOT NULL AND idempotency_key IS NOT NULL");
        builder.HasIndex(x => x.EventKey).IsUnique().HasDatabaseName("ux_audit_event_key").HasFilter("event_key IS NOT NULL");
        builder.HasIndex(x => new { x.TraceId, x.CreatedAt, x.Id }).HasDatabaseName("ix_audit_trace_id")
            .HasFilter("trace_id IS NOT NULL");
    }
}
