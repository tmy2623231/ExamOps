using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class ResultReleaseConfiguration : IEntityTypeConfiguration<ResultRelease>
{
    public void Configure(EntityTypeBuilder<ResultRelease> builder)
    {
        builder.ToTable("result_release");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.TaskId).HasColumnName("task_id");
        builder.Property(x => x.ReleaseNo).HasColumnName("release_no");
        builder.Property(x => x.ScorePolicy).HasColumnName("score_policy").HasMaxLength(16).IsRequired();
        builder.Property(x => x.PassScore).HasColumnName("pass_score").HasPrecision(6, 2);
        builder.Property(x => x.ResultReleasePolicy).HasColumnName("result_release_policy").HasMaxLength(16).IsRequired();
        builder.Property(x => x.SummaryJson).HasColumnName("summary_json").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.PublishedBy).HasColumnName("published_by");
        builder.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamptz");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).IsRequired();
        builder.HasOne<ExamTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.PublishedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.TaskId, x.ReleaseNo }).IsUnique().HasDatabaseName("ux_result_release_no");
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_result_release_idempotency");
        builder.HasIndex(x => new { x.Id, x.TaskId }).IsUnique().HasDatabaseName("ux_result_release_identity");
        builder.HasIndex(x => new { x.TaskId, x.PublishedAt, x.Id })
            .HasDatabaseName("ix_result_release_task_published").IsDescending(false, true, true);
        builder.HasIndex(x => x.PublishedBy).HasDatabaseName("ix_result_release_published_by");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_result_release_no", "release_no > 0");
            t.HasCheckConstraint("ck_result_release_policy", "score_policy IN ('Latest','Highest','First','Average')");
            t.HasCheckConstraint("ck_result_release_mode", "result_release_policy IN ('Immediate','AfterClose','Manual')");
            t.HasCheckConstraint("ck_result_release_summary", "jsonb_typeof(summary_json) = 'object'");
            t.HasCheckConstraint("ck_result_release_hash", "payload_sha256 ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_result_release_pass_score", "pass_score IS NULL OR (pass_score >= 0 AND pass_score <= 9999.99)");
        });
    }
}

public sealed class ResultReleaseItemConfiguration : IEntityTypeConfiguration<ResultReleaseItem>
{
    public void Configure(EntityTypeBuilder<ResultReleaseItem> builder)
    {
        builder.ToTable("result_release_item");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.ReleaseId).HasColumnName("release_id");
        builder.Property(x => x.TaskId).HasColumnName("task_id");
        builder.Property(x => x.AssignmentId).HasColumnName("assignment_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.DepartmentIdSnapshot).HasColumnName("department_id_snapshot");
        builder.Property(x => x.AttemptId).HasColumnName("attempt_id");
        builder.Property(x => x.FinalScore).HasColumnName("final_score").HasPrecision(6, 2);
        builder.Property(x => x.MaxScore).HasColumnName("max_score").HasPrecision(6, 2);
        builder.Property(x => x.Passed).HasColumnName("passed");
        builder.Property(x => x.SourceJson).HasColumnName("source_json").HasColumnType("jsonb").IsRequired();
        builder.HasOne<ResultRelease>().WithMany().HasForeignKey(x => new { x.ReleaseId, x.TaskId })
            .HasPrincipalKey(x => new { x.Id, x.TaskId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExamAssignment>().WithMany().HasForeignKey(x => new { x.AssignmentId, x.TaskId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.TaskId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentIdSnapshot).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExamAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ReleaseId, x.AssignmentId }).IsUnique().HasDatabaseName("ux_result_release_item_assignment");
        builder.HasIndex(x => new { x.UserId, x.ReleaseId }).HasDatabaseName("ix_result_release_item_user");
        builder.HasIndex(x => new { x.DepartmentIdSnapshot, x.ReleaseId }).HasDatabaseName("ix_result_release_item_department");
        builder.HasIndex(x => x.AttemptId).HasDatabaseName("ix_result_release_item_attempt");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_result_release_item_score",
                "(final_score IS NULL AND max_score IS NULL AND passed IS NULL AND attempt_id IS NULL) OR " +
                "(final_score IS NOT NULL AND max_score IS NOT NULL AND attempt_id IS NOT NULL AND " +
                "final_score >= 0 AND final_score <= max_score AND max_score <= 9999.99)");
            t.HasCheckConstraint("ck_result_release_item_source", "jsonb_typeof(source_json) = 'object'");
        });
    }
}

public sealed class ReportExportConfiguration : IEntityTypeConfiguration<ReportExport>
{
    public void Configure(EntityTypeBuilder<ReportExport> builder)
    {
        builder.ToTable("report_export");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.TaskId).HasColumnName("task_id");
        builder.Property(x => x.ReportType).HasColumnName("report_type").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Format).HasColumnName("format").HasMaxLength(8).IsRequired();
        builder.Property(x => x.FileKey).HasColumnName("file_key").IsRequired();
        builder.Property(x => x.FileName).HasColumnName("file_name").IsRequired();
        builder.Property(x => x.RowCount).HasColumnName("row_count");
        builder.Property(x => x.FilterJson).HasColumnName("filter_json").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
        builder.HasOne<ExamTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.FileKey).IsUnique().HasDatabaseName("ux_report_export_key");
        builder.HasIndex(x => new { x.CreatedBy, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_report_export_creator_created").IsDescending(false, true, true);
        builder.HasIndex(x => new { x.TaskId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_report_export_task_created").IsDescending(false, true, true);
        builder.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_report_export_expires");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_report_export_type",
                "report_type IN ('Summary','Distribution','PassRate','Departments','Questions','Anomalies','Attempts')");
            t.HasCheckConstraint("ck_report_export_format", "format IN ('CSV','XLSX')");
            t.HasCheckConstraint("ck_report_export_rows", "row_count >= 0");
            t.HasCheckConstraint("ck_report_export_filter", "jsonb_typeof(filter_json) = 'object'");
            t.HasCheckConstraint("ck_report_export_key", "file_key !~ '^/' AND file_key !~ '^[A-Za-z]:'");
        });
    }
}
