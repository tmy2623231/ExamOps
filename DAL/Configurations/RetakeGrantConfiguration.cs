using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class RetakeGrantConfiguration : IEntityTypeConfiguration<RetakeGrant>
{
    public void Configure(EntityTypeBuilder<RetakeGrant> builder)
    {
        // Append-only grant: no concurrency token or update-specific assumptions.
        builder.ToTable("retake_grant");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.AssignmentId).HasColumnName("assignment_id");
        builder.Property(x => x.ExtraAttempts).HasColumnName("extra_attempts");
        builder.Property(x => x.Reason).HasColumnName("reason").IsRequired();
        builder.Property(x => x.GrantedBy).HasColumnName("granted_by");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.HasOne<ExamAssignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.GrantedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_retake_grant_idempotency");
        builder.HasIndex(x => x.AssignmentId).HasDatabaseName("ix_retake_grant_assignment_id");
        builder.HasIndex(x => x.GrantedBy).HasDatabaseName("ix_retake_grant_granted_by");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_retake_grant_extra", "extra_attempts > 0");
            t.HasCheckConstraint("ck_retake_grant_reason", "char_length(reason) > 0");
        });
    }
}
