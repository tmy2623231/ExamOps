using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        builder.ToTable("import_batch");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.BankId).HasColumnName("bank_id");
        builder.Property(x => x.FileName).HasColumnName("file_name").IsRequired();
        builder.Property(x => x.FileKey).HasColumnName("file_key").IsRequired();
        builder.Property(x => x.FileHash).HasColumnName("file_hash").HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(x => x.Mode).HasColumnName("mode").HasDefaultValue("AllOrNothing").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Uploaded").IsRequired();
        builder.Property(x => x.TotalRows).HasColumnName("total_rows").HasDefaultValue(0);
        builder.Property(x => x.ValidRows).HasColumnName("valid_rows").HasDefaultValue(0);
        builder.Property(x => x.InvalidRows).HasColumnName("invalid_rows").HasDefaultValue(0);
        builder.Property(x => x.WarningRows).HasColumnName("warning_rows").HasDefaultValue(0);
        builder.Property(x => x.DuplicateRows).HasColumnName("duplicate_rows").HasDefaultValue(0);
        builder.Property(x => x.ImportedRows).HasColumnName("imported_rows").HasDefaultValue(0);
        builder.Property(x => x.FailedRows).HasColumnName("failed_rows").HasDefaultValue(0);
        builder.Property(x => x.ExcludedRows).HasColumnName("excluded_rows").HasDefaultValue(0);
        builder.Property(x => x.WarningJson).HasColumnName("warning_json").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();
        builder.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<QuestionBank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.BankId, x.FileHash }).IsUnique().HasDatabaseName("uq_import_batch_hash");
        builder.HasIndex(x => new { x.BankId, x.CreatedAt, x.Id }).HasDatabaseName("ix_import_batch_bank_created")
            .IsDescending(false, true, true);
        builder.HasIndex(x => x.CreatedBy).HasDatabaseName("ix_import_batch_created_by");
        builder.HasIndex(x => new { x.Status, x.CreatedAt, x.Id }).HasDatabaseName("ix_import_batch_status_created")
            .IsDescending(false, true, true);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_import_batch_file_hash", "file_hash ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_import_batch_mode", "mode IN ('AllOrNothing','SkipInvalid')");
            t.HasCheckConstraint("ck_import_batch_status_values", "status IN ('Uploaded','Validating','Previewed','Importing','Completed','PartiallyFailed','Failed')");
            t.HasCheckConstraint("ck_import_batch_counts_nonnegative",
                "total_rows >= 0 AND valid_rows >= 0 AND invalid_rows >= 0 AND warning_rows >= 0 AND duplicate_rows >= 0 AND " +
                "imported_rows >= 0 AND failed_rows >= 0 AND excluded_rows >= 0 AND total_rows <= 5000 AND " +
                "warning_rows <= valid_rows AND duplicate_rows <= invalid_rows AND imported_rows + failed_rows + excluded_rows <= total_rows");
            t.HasCheckConstraint("ck_import_batch_preview_counts",
                "status NOT IN ('Uploaded','Validating','Previewed') OR (valid_rows + invalid_rows = total_rows)");
            t.HasCheckConstraint("ck_import_batch_terminal_counts",
                "status NOT IN ('Completed','PartiallyFailed','Failed') OR (imported_rows + failed_rows + excluded_rows = total_rows)");
            t.HasCheckConstraint("ck_import_batch_confirmed",
                "status NOT IN ('Importing','Completed','PartiallyFailed','Failed') OR confirmed_at IS NOT NULL");
            t.HasCheckConstraint("ck_import_batch_finished",
                "status NOT IN ('Completed','PartiallyFailed','Failed') OR finished_at IS NOT NULL");
        });
    }
}
