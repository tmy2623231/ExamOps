using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class RevocationRecordConfiguration : IEntityTypeConfiguration<RevocationRecord>
{
    public void Configure(EntityTypeBuilder<RevocationRecord> builder)
    {
        // Append-only record: no concurrency token or update-specific assumptions.
        builder.ToTable("revocation_record");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.TargetType).HasColumnName("target_type").IsRequired();
        builder.Property(x => x.TargetId).HasColumnName("target_id");
        builder.Property(x => x.Scope).HasColumnName("scope").IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason").IsRequired();
        builder.Property(x => x.ImpactJson).HasColumnName("impact_json").HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.Property(x => x.ActorId).HasColumnName("actor_id");
        builder.Property(x => x.ActorRole).HasColumnName("actor_role").HasDefaultValue("User").IsRequired();
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_revocation_idempotency");
        builder.HasIndex(x => new { x.TargetType, x.TargetId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_revocation_entity").IsDescending(false, false, true, true);
        builder.HasIndex(x => x.ActorId).HasDatabaseName("ix_revocation_actor_id");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_revocation_target", "target_type IN ('ExamTask','ExamAssignment','ExamAttempt','AssignmentBatch')");
            t.HasCheckConstraint("ck_revocation_scope", "scope IN ('KeepScore','VoidScore','ForceSubmit','VoidAttempt')");
            t.HasCheckConstraint("ck_revocation_reason", "char_length(reason) >= 10");
            t.HasCheckConstraint("ck_revocation_actor", "actor_id IS NOT NULL OR actor_role = 'System'");
            t.HasCheckConstraint("ck_revocation_actor_role", "actor_role IN ('User','System')");
        });
    }
}
