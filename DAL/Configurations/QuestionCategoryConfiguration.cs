using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class QuestionCategoryConfiguration : IEntityTypeConfiguration<QuestionCategory>
{
    public void Configure(EntityTypeBuilder<QuestionCategory> builder)
    {
        builder.ToTable("question_category");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.BankId).HasColumnName("bank_id");
        builder.Property(x => x.ParentId).HasColumnName("parent_id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<QuestionBank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<QuestionCategory>().WithMany().HasForeignKey(x => new { x.BankId, x.ParentId })
            .HasPrincipalKey(x => new { x.BankId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.BankId, x.Id }).IsUnique().HasDatabaseName("uq_question_category_owner");
        builder.HasIndex(x => new { x.BankId, x.Name }).IsUnique().HasDatabaseName("ux_question_category_root_name")
            .HasFilter("parent_id IS NULL");
        builder.HasIndex(x => new { x.BankId, x.ParentId, x.Name }).IsUnique()
            .HasDatabaseName("ux_question_category_child_name").HasFilter("parent_id IS NOT NULL");
        builder.HasIndex(x => new { x.BankId, x.ParentId }).HasDatabaseName("ix_question_category_bank_parent");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_question_category_parent", "parent_id IS NULL OR parent_id <> id");
            t.HasCheckConstraint("ck_question_category_name", "length(btrim(name)) > 0");
        });
    }
}
