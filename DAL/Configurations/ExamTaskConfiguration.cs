using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class ExamTaskConfiguration : IEntityTypeConfiguration<ExamTask>
{
    public void Configure(EntityTypeBuilder<ExamTask> builder)
    {
        builder.ToTable("exam_task");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.DepartmentId).HasColumnName("department_id");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").IsRequired();
        builder.Property(x => x.Description).HasColumnName("description");
        builder.Property(x => x.ExamDate).HasColumnName("exam_date").HasColumnType("date");
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Draft").IsRequired();
        builder.Property(x => x.PaperVersionId).HasColumnName("paper_version_id");
        builder.Property(x => x.AvailableFrom).HasColumnName("available_from").HasColumnType("timestamp without time zone");
        builder.Property(x => x.AvailableTo).HasColumnName("available_to").HasColumnType("timestamp without time zone");
        builder.Property(x => x.DurationMinutes).HasColumnName("duration_minutes").HasDefaultValue(60);
        builder.Property(x => x.AttemptLimit).HasColumnName("attempt_limit").HasDefaultValue(1);
        builder.Property(x => x.ScorePolicy).HasColumnName("score_policy").HasDefaultValue("Latest").IsRequired();
        builder.Property(x => x.RetakeApproval).HasColumnName("retake_approval").HasDefaultValue("None").IsRequired();
        builder.Property(x => x.RetakeCooldownMinutes).HasColumnName("retake_cooldown_minutes").HasDefaultValue(0);
        builder.Property(x => x.RetakeDeadlineAt).HasColumnName("retake_deadline_at").HasColumnType("timestamptz");
        builder.Property(x => x.PassScore).HasColumnName("pass_score").HasPrecision(6, 2);
        builder.Property(x => x.ResultReleasePolicy).HasColumnName("result_release_policy").HasDefaultValue("AfterClose").IsRequired();
        builder.Property(x => x.ResultStatus).HasColumnName("result_status").HasDefaultValue("Unpublished").IsRequired();
        builder.Property(x => x.ResultReleaseId).HasColumnName("result_release_id");
        builder.Property(x => x.ResultPublishedAt).HasColumnName("result_published_at").HasColumnType("timestamptz");
        builder.Property(x => x.ResultWithdrawnAt).HasColumnName("result_withdrawn_at").HasColumnType("timestamptz");
        builder.Property(x => x.ResultWithdrawReason).HasColumnName("result_withdraw_reason");
        builder.Property(x => x.LateScorePolicy).HasColumnName("late_score_policy").HasDefaultValue("Score").IsRequired();
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamptz");
        builder.Property(x => x.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
        builder.Property(x => x.ArchivedAt).HasColumnName("archived_at").HasColumnType("timestamptz");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaperVersion>().WithMany().HasForeignKey(x => x.PaperVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        // The database also enforces (result_release_id, id) -> result_release(id, task_id).
        // Mapping only the nullable pointer keeps ExamTask.Id database-generated.
        builder.HasOne<ResultRelease>().WithMany().HasForeignKey(x => x.ResultReleaseId)
            .HasConstraintName("fk_exam_task_result_release").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_exam_task_code");
        builder.HasIndex(x => x.CreatedBy).HasDatabaseName("ix_exam_task_created_by");
        builder.HasIndex(x => x.PaperVersionId).HasDatabaseName("ix_exam_task_paper_version_id");
        builder.HasIndex(x => new { x.DepartmentId, x.Status, x.AvailableFrom, x.AvailableTo })
            .HasDatabaseName("ix_exam_task_department_status");
        builder.HasIndex(x => new { x.Status, x.AvailableFrom, x.AvailableTo })
            .HasDatabaseName("ix_exam_task_status_window");
        builder.HasIndex(x => x.ResultReleaseId).HasDatabaseName("ix_exam_task_result_release");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_exam_task_status", "status IN ('Draft','Published','Closed','Revoked','Archived')");
            t.HasCheckConstraint("ck_exam_task_window", "available_to > available_from");
            t.HasCheckConstraint("ck_exam_task_duration", "duration_minutes > 0 AND duration_minutes <= 1440");
            t.HasCheckConstraint("ck_exam_task_attempts", "attempt_limit > 0");
            t.HasCheckConstraint("ck_exam_task_score_policy", "score_policy IN ('Latest','Highest','First','Average')");
            t.HasCheckConstraint("ck_exam_task_approval", "retake_approval IN ('None','Manager')");
            t.HasCheckConstraint("ck_exam_task_release", "result_release_policy IN ('Immediate','AfterClose','Manual')");
            t.HasCheckConstraint("ck_exam_task_pass_score", "pass_score IS NULL OR (pass_score >= 0 AND pass_score <= 9999.99)");
            t.HasCheckConstraint("ck_exam_task_result_status", "result_status IN ('Unpublished','Published','Withdrawn')");
            t.HasCheckConstraint("ck_exam_task_late_policy", "late_score_policy IN ('Score','Zero')");
            t.HasCheckConstraint("ck_exam_task_retake_cooldown", "retake_cooldown_minutes >= 0");
            t.HasCheckConstraint("ck_exam_task_published_ref", "status = 'Draft' OR paper_version_id IS NOT NULL");
            t.HasCheckConstraint("ck_exam_task_result_timestamp", "result_status = 'Unpublished' OR result_published_at IS NOT NULL");
            t.HasCheckConstraint("ck_exam_task_result_release_state",
                "(result_status = 'Published' AND result_release_id IS NOT NULL AND result_published_at IS NOT NULL " +
                "AND result_withdrawn_at IS NULL AND result_withdraw_reason IS NULL) OR " +
                "(result_status = 'Withdrawn' AND result_release_id IS NOT NULL AND result_published_at IS NOT NULL " +
                "AND result_withdrawn_at IS NOT NULL AND char_length(result_withdraw_reason) >= 10) OR " +
                "(result_status = 'Unpublished' AND result_release_id IS NULL AND result_published_at IS NULL " +
                "AND result_withdrawn_at IS NULL AND result_withdraw_reason IS NULL)");
        });
    }
}
