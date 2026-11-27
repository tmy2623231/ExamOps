using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class ExamAssignmentConfiguration : IEntityTypeConfiguration<ExamAssignment>
{
    public void Configure(EntityTypeBuilder<ExamAssignment> builder)
    {
        builder.ToTable("exam_assignment");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.BatchId).HasColumnName("batch_id");
        builder.Property(x => x.TaskId).HasColumnName("task_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.DepartmentIdSnapshot).HasColumnName("department_id_snapshot");
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Pending").IsRequired();
        builder.Property(x => x.AvailableFrom).HasColumnName("available_from").HasColumnType("timestamp without time zone");
        builder.Property(x => x.AvailableTo).HasColumnName("available_to").HasColumnType("timestamp without time zone");
        builder.Property(x => x.AttemptLimitOverride).HasColumnName("attempt_limit_override");
        builder.Property(x => x.DurationMinutesOverride).HasColumnName("duration_minutes_override");
        builder.Property(x => x.AttemptsUsed).HasColumnName("attempts_used").HasDefaultValue(0);
        builder.Property(x => x.InheritedAttemptsUsed).HasColumnName("inherited_attempts_used").HasDefaultValue(0);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
        builder.Property(x => x.RevokeReason).HasColumnName("revoke_reason");
        builder.Property(x => x.ReissuedFromId).HasColumnName("reissued_from_id");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<AssignmentBatch>().WithMany()
            .HasForeignKey(x => new { x.BatchId, x.TaskId })
            .HasPrincipalKey(x => new { x.Id, x.TaskId })
            .HasConstraintName("fk_exam_assignment_batch_owner").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExamTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentIdSnapshot).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExamAssignment>().WithMany()
            .HasForeignKey(x => new { x.ReissuedFromId, x.TaskId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.TaskId, x.UserId })
            .HasConstraintName("fk_exam_assignment_reissued_from").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.Id, x.TaskId, x.UserId }).IsUnique()
            .HasDatabaseName("uq_exam_assignment_identity");
        builder.HasIndex(x => new { x.ReissuedFromId, x.TaskId, x.UserId }).IsUnique()
            .HasDatabaseName("uq_exam_assignment_reissue");
        builder.HasIndex(x => new { x.TaskId, x.UserId }).IsUnique()
            .HasDatabaseName("ux_exam_assignment_active").HasFilter("status <> 'Revoked'");
        builder.HasIndex(x => new { x.UserId, x.Status }).HasDatabaseName("ix_exam_assignment_user_status");
        builder.HasIndex(x => new { x.DepartmentIdSnapshot, x.Status }).HasDatabaseName("ix_exam_assignment_department_status");
        builder.HasIndex(x => x.BatchId).HasDatabaseName("ix_exam_assignment_batch_id");
        builder.HasIndex(x => x.TaskId).HasDatabaseName("ix_exam_assignment_task_id");
        builder.HasIndex(x => x.ReissuedFromId).HasDatabaseName("ix_exam_assignment_reissued_from_id");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_exam_assignment_status", "status IN ('Pending','Available','InProgress','Submitted','Graded','Closed','Expired','Revoked')");
            t.HasCheckConstraint("ck_exam_assignment_window", "available_to > available_from");
            t.HasCheckConstraint("ck_exam_assignment_attempts",
                "attempts_used >= 0 AND inherited_attempts_used >= 0 AND " +
                "(attempt_limit_override IS NULL OR attempt_limit_override > 0) AND " +
                "(duration_minutes_override IS NULL OR (duration_minutes_override > 0 AND duration_minutes_override <= 1440))");
            t.HasCheckConstraint("ck_exam_assignment_revoked",
                "(status = 'Revoked' AND revoked_at IS NOT NULL AND char_length(revoke_reason) >= 10) OR " +
                "(status <> 'Revoked' AND revoked_at IS NULL AND revoke_reason IS NULL)");
        });
    }
}
