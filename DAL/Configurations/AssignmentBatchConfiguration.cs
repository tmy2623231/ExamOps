using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class AssignmentBatchConfiguration : IEntityTypeConfiguration<AssignmentBatch>
{
    public void Configure(EntityTypeBuilder<AssignmentBatch> builder)
    {
        builder.ToTable("assignment_batch");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.TaskId).HasColumnName("task_id");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).IsRequired();
        builder.Property(x => x.TargetJson).HasColumnName("target_json").HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Pending").IsRequired();
        builder.Property(x => x.TotalCount).HasColumnName("total_count").HasDefaultValue(0);
        builder.Property(x => x.SuccessCount).HasColumnName("success_count").HasDefaultValue(0);
        builder.Property(x => x.FailedCount).HasColumnName("failed_count").HasDefaultValue(0);
        builder.Property(x => x.SkippedCount).HasColumnName("skipped_count").HasDefaultValue(0);
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamptz");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<ExamTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_assignment_batch_idempotency");
        builder.HasIndex(x => new { x.Id, x.TaskId }).IsUnique().HasDatabaseName("uq_assignment_batch_identity");
        builder.HasIndex(x => new { x.TaskId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_assignment_batch_task_created").IsDescending(false, true, true);
        builder.HasIndex(x => x.CreatedBy).HasDatabaseName("ix_assignment_batch_created_by");
        builder.HasIndex(x => new { x.Status, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_assignment_batch_status_created").IsDescending(false, true, true);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_assignment_batch_status_values", "status IN ('Pending','Running','Completed','PartiallyFailed','Failed','Revoking','Revoked')");
            t.HasCheckConstraint("ck_assignment_batch_target", "jsonb_typeof(target_json) = 'object'");
            t.HasCheckConstraint("ck_assignment_batch_counts",
                "total_count >= 0 AND success_count >= 0 AND failed_count >= 0 AND skipped_count >= 0 AND " +
                "success_count + failed_count + skipped_count <= total_count");
            t.HasCheckConstraint("ck_assignment_batch_terminal_counts",
                "status NOT IN ('Completed','PartiallyFailed','Failed','Revoked') OR " +
                "success_count + failed_count + skipped_count = total_count");
            t.HasCheckConstraint("ck_assignment_batch_started", "status = 'Pending' OR started_at IS NOT NULL");
            t.HasCheckConstraint("ck_assignment_batch_finished",
                "status NOT IN ('Completed','PartiallyFailed','Failed','Revoked') OR finished_at IS NOT NULL");
        });
    }
}
