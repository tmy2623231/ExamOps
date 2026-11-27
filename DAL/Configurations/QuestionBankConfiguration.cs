using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class QuestionBankConfiguration : IEntityTypeConfiguration<QuestionBank>
{
    public void Configure(EntityTypeBuilder<QuestionBank> builder)
    {
        builder.ToTable("question_bank");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.DepartmentId).HasColumnName("department_id");
        builder.Property(x => x.IsShared).HasColumnName("is_shared").HasDefaultValue(false);
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Active").IsRequired();
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.DepartmentId).HasDatabaseName("ix_question_bank_department_id");
        builder.HasIndex(x => x.CreatedBy).HasDatabaseName("ix_question_bank_created_by");
        builder.ToTable(t => t.HasCheckConstraint("ck_question_bank_status", "status IN ('Active','Archived')"));
    }
}
