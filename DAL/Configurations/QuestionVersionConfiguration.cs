using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class QuestionVersionConfiguration : IEntityTypeConfiguration<QuestionVersion>
{
    public void Configure(EntityTypeBuilder<QuestionVersion> builder)
    {
        builder.ToTable("question_version");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.QuestionId).HasColumnName("question_id");
        builder.Property(x => x.VersionNo).HasColumnName("version_no");
        builder.Property(x => x.Stem).HasColumnName("stem").IsRequired();
        builder.Property(x => x.AnswerJson).HasColumnName("answer_json").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.Property(x => x.Difficulty).HasColumnName("difficulty").HasDefaultValue("Medium").IsRequired();
        builder.Property(x => x.DefaultScore).HasColumnName("default_score").HasPrecision(6, 2).HasDefaultValue(1);
        builder.Property(x => x.Analysis).HasColumnName("analysis");
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Draft").IsRequired();
        builder.Property(x => x.Source).HasColumnName("source").HasDefaultValue("Manual").IsRequired();
        builder.Property(x => x.ImportBatchId).HasColumnName("import_batch_id");
        builder.Property(x => x.ImportRowId).HasColumnName("import_row_id");
        builder.Property(x => x.PublishedAt).HasColumnName("published_at");
        builder.Property(x => x.PublishedBy).HasColumnName("published_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<Question>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportRow>().WithMany().HasForeignKey(x => x.ImportRowId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportRow>().WithMany().HasForeignKey(x => new { x.ImportBatchId, x.ImportRowId })
            .HasPrincipalKey(x => new { x.BatchId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.PublishedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.QuestionId, x.VersionNo }).IsUnique().HasDatabaseName("uq_question_version_no");
        builder.HasIndex(x => new { x.QuestionId, x.Id }).IsUnique().HasDatabaseName("uq_question_version_owner");
        builder.HasIndex(x => new { x.QuestionId, x.VersionNo }).HasDatabaseName("ix_question_version_question_id")
            .IsDescending(false, true);
        builder.HasIndex(x => x.PublishedBy).HasDatabaseName("ix_question_version_published_by");
        builder.HasIndex(x => x.ImportBatchId).HasDatabaseName("ix_question_version_import_batch_id");
        builder.HasIndex(x => x.ImportRowId).HasDatabaseName("ix_question_version_import_row_id");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_question_version_status", "status IN ('Draft','Published')");
            t.HasCheckConstraint("ck_question_version_difficulty", "difficulty IN ('Easy','Medium','Hard')");
            t.HasCheckConstraint("ck_question_version_default_score", "default_score > 0 AND default_score <= 9999.99");
            t.HasCheckConstraint("ck_question_version_source", "source IN ('Manual','Import')");
            t.HasCheckConstraint("ck_question_version_import_refs",
                "(source = 'Manual' AND import_batch_id IS NULL AND import_row_id IS NULL) OR " +
                "(source = 'Import' AND import_batch_id IS NOT NULL AND import_row_id IS NOT NULL)");
        });
    }
}
