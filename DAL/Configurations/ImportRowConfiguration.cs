using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class ImportRowConfiguration : IEntityTypeConfiguration<ImportRow>
{
    public void Configure(EntityTypeBuilder<ImportRow> builder)
    {
        builder.ToTable("import_row");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.BatchId).HasColumnName("batch_id");
        builder.Property(x => x.RowNo).HasColumnName("row_no");
        builder.Property(x => x.ExternalCode).HasColumnName("external_code").HasMaxLength(128);
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.RawJson).HasColumnName("raw_json").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.Property(x => x.AnswerSummary).HasColumnName("answer_summary");
        builder.Property(x => x.ErrorJson).HasColumnName("error_json").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();
        builder.Property(x => x.WarningJson).HasColumnName("warning_json").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();
        builder.Property(x => x.QuestionId).HasColumnName("question_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Question>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.BatchId, x.RowNo }).IsUnique().HasDatabaseName("uq_import_row_no");
        builder.HasIndex(x => new { x.BatchId, x.Id }).IsUnique().HasDatabaseName("uq_import_row_owner");
        builder.HasIndex(x => new { x.BatchId, x.Status, x.RowNo }).HasDatabaseName("ix_import_row_batch_status");
        builder.HasIndex(x => x.QuestionId).HasDatabaseName("ix_import_row_question_id");
        builder.HasIndex(x => new { x.BatchId, x.ExternalCode }).HasDatabaseName("ix_import_row_external_code");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_import_row_status_values", "status IN ('Valid','Invalid','Warning','Imported','Excluded')");
            t.HasCheckConstraint("ck_import_row_raw_json", "jsonb_typeof(raw_json) = 'object'");
            t.HasCheckConstraint("ck_import_row_error_json", "jsonb_typeof(error_json) = 'array'");
            t.HasCheckConstraint("ck_import_row_warning_json", "jsonb_typeof(warning_json) = 'array'");
            t.HasCheckConstraint("ck_import_row_question_only_imported", "status = 'Imported' OR question_id IS NULL");
        });
    }
}
