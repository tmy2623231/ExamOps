using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class AssignmentBatchRowConfiguration : IEntityTypeConfiguration<AssignmentBatchRow>
{
    public void Configure(EntityTypeBuilder<AssignmentBatchRow> builder)
    {
        builder.ToTable("assignment_batch_row");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.BatchId).HasColumnName("batch_id");
        builder.Property(x => x.RowNo).HasColumnName("row_no");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.TargetJson).HasColumnName("target_json").HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Pending").IsRequired();
        builder.Property(x => x.AssignmentId).HasColumnName("assignment_id");
        builder.Property(x => x.ErrorJson).HasColumnName("error_json").HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<AssignmentBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExamAssignment>().WithMany().HasForeignKey(x => x.AssignmentId)
            .HasConstraintName("fk_assignment_batch_row_assignment").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.BatchId, x.RowNo }).IsUnique().HasDatabaseName("ux_assignment_batch_row_no");
        builder.HasIndex(x => new { x.BatchId, x.UserId }).IsUnique().HasDatabaseName("ux_assignment_batch_row_user");
        builder.HasIndex(x => new { x.BatchId, x.Status, x.RowNo })
            .HasDatabaseName("ix_assignment_batch_row_batch_status");
        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_assignment_batch_row_user_id");
        builder.HasIndex(x => x.AssignmentId).HasDatabaseName("ix_assignment_batch_row_assignment_id");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_assignment_batch_row_no_positive", "row_no > 0");
            t.HasCheckConstraint("ck_assignment_batch_row_target", "jsonb_typeof(target_json) = 'object'");
            t.HasCheckConstraint("ck_assignment_batch_row_error", "jsonb_typeof(error_json) = 'array'");
            t.HasCheckConstraint("ck_assignment_batch_row_status", "status IN ('Pending','Processing','Success','Skipped','Failed','Revoked')");
            t.HasCheckConstraint("ck_assignment_batch_row_result",
                "(status = 'Success' AND assignment_id IS NOT NULL) OR " +
                "(status IN ('Pending','Processing','Skipped','Failed') AND assignment_id IS NULL) OR status = 'Revoked'");
        });
    }
}
