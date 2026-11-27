using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class PaperConfiguration : IEntityTypeConfiguration<Paper>
{
    public void Configure(EntityTypeBuilder<Paper> builder)
    {
        builder.ToTable("paper");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.Title).HasColumnName("title").IsRequired();
        builder.Property(x => x.DepartmentId).HasColumnName("department_id");
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Draft").IsRequired();
        builder.Property(x => x.CurrentVersionId).HasColumnName("current_version_id");
        builder.Property(x => x.ArchivedAt).HasColumnName("archived_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaperVersion>().WithMany().HasForeignKey(x => x.CurrentVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.DepartmentId).HasDatabaseName("ix_paper_department_id");
        builder.HasIndex(x => x.CurrentVersionId).HasDatabaseName("ix_paper_current_version_id");
        builder.HasIndex(x => new { x.Status, x.DepartmentId }).HasDatabaseName("ix_paper_status_department");
        builder.ToTable(t => t.HasCheckConstraint("ck_paper_status", "status IN ('Draft','Published','Archived')"));
    }
}
