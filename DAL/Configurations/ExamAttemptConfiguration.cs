using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class ExamAttemptConfiguration : IEntityTypeConfiguration<ExamAttempt>
{
    public void Configure(EntityTypeBuilder<ExamAttempt> builder)
    {
        builder.ToTable("exam_attempt");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.AssignmentId).HasColumnName("assignment_id");
        builder.Property(x => x.AttemptNo).HasColumnName("attempt_no");
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("InProgress").IsRequired();
        builder.Property(x => x.StartKey).HasColumnName("start_key").HasMaxLength(128).IsRequired();
        builder.Property(x => x.SubmitKey).HasColumnName("submit_key").HasMaxLength(128);
        builder.Property(x => x.PaperSnapshotJson).HasColumnName("paper_snapshot_json").HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.DeadlineAt).HasColumnName("deadline_at").HasColumnType("timestamptz");
        builder.Property(x => x.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
        builder.Property(x => x.AutoScore).HasColumnName("auto_score").HasPrecision(6, 2);
        builder.Property(x => x.Score).HasColumnName("score").HasPrecision(6, 2);
        builder.Property(x => x.MaxScore).HasColumnName("max_score").HasPrecision(6, 2).HasDefaultValue(0);
        builder.Property(x => x.GradedAt).HasColumnName("graded_at").HasColumnType("timestamptz");
        builder.Property(x => x.GradedBy).HasColumnName("graded_by");
        builder.Property(x => x.IsLate).HasColumnName("is_late").HasDefaultValue(false);
        builder.Property(x => x.VoidedAt).HasColumnName("voided_at").HasColumnType("timestamptz");
        builder.Property(x => x.VoidReason).HasColumnName("void_reason");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<ExamAssignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.GradedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.AssignmentId, x.AttemptNo }).IsUnique().HasDatabaseName("uq_exam_attempt_no");
        builder.HasIndex(x => x.StartKey).IsUnique().HasDatabaseName("ux_exam_attempt_start_key");
        builder.HasIndex(x => x.SubmitKey).IsUnique().HasDatabaseName("ux_exam_attempt_submit_key");
        builder.HasIndex(x => x.AssignmentId).IsUnique().HasDatabaseName("ux_exam_attempt_one_in_progress")
            .HasFilter("status = 'InProgress'");
        builder.HasIndex(x => new { x.Status, x.DeadlineAt, x.Id }).HasDatabaseName("ix_exam_attempt_status_deadline");
        builder.HasIndex(x => x.GradedBy).HasDatabaseName("ix_exam_attempt_graded_by");
        builder.HasIndex(x => new { x.AssignmentId, x.AttemptNo }).HasDatabaseName("ix_exam_attempt_assignment_no");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_exam_attempt_no", "attempt_no > 0");
            t.HasCheckConstraint("ck_exam_attempt_status", "status IN ('InProgress','Submitted','AutoSubmitted','Graded','Voided')");
            t.HasCheckConstraint("ck_exam_attempt_deadline", "deadline_at > started_at");
            t.HasCheckConstraint("ck_exam_attempt_time_order",
                "(submitted_at IS NULL OR submitted_at >= started_at) AND " +
                "(graded_at IS NULL OR (submitted_at IS NOT NULL AND graded_at >= submitted_at)) AND " +
                "(status <> 'AutoSubmitted' OR submitted_at >= deadline_at + interval '30 seconds')");
            t.HasCheckConstraint("ck_exam_attempt_score",
                "max_score >= 0 AND max_score <= 9999.99 AND " +
                "(auto_score IS NULL OR (auto_score >= 0 AND auto_score <= max_score)) AND " +
                "(score IS NULL OR (score >= 0 AND score <= max_score))");
            t.HasCheckConstraint("ck_exam_attempt_snapshot",
                "jsonb_typeof(paper_snapshot_json) = 'object' AND paper_snapshot_json ? 'sections' AND " +
                "jsonb_typeof(paper_snapshot_json -> 'sections') = 'array'");
            t.HasCheckConstraint("ck_exam_attempt_submission",
                "status NOT IN ('Submitted','AutoSubmitted','Graded') OR (submitted_at IS NOT NULL AND submit_key IS NOT NULL)");
            t.HasCheckConstraint("ck_exam_attempt_in_progress",
                "status <> 'InProgress' OR (submitted_at IS NULL AND submit_key IS NULL AND auto_score IS NULL AND score IS NULL)");
            t.HasCheckConstraint("ck_exam_attempt_graded",
                "status <> 'Graded' OR (graded_at IS NOT NULL AND auto_score IS NOT NULL AND score IS NOT NULL)");
            t.HasCheckConstraint("ck_exam_attempt_auto_submitted", "status <> 'AutoSubmitted' OR is_late");
            t.HasCheckConstraint("ck_exam_attempt_voided",
                "status <> 'Voided' OR (voided_at IS NOT NULL AND char_length(void_reason) >= 10)");
        });
    }
}
