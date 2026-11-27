using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class QuestionCategoryLinkConfiguration : IEntityTypeConfiguration<QuestionCategoryLink>
{
    public void Configure(EntityTypeBuilder<QuestionCategoryLink> builder)
    {
        builder.ToTable("question_category_link");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.QuestionVersionId).HasColumnName("question_version_id");
        builder.Property(x => x.CategoryId).HasColumnName("category_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.HasOne<QuestionVersion>().WithMany().HasForeignKey(x => x.QuestionVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<QuestionCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.QuestionVersionId, x.CategoryId }).IsUnique()
            .HasDatabaseName("ux_question_category_link_pair");
        builder.HasIndex(x => x.QuestionVersionId).HasDatabaseName("ix_question_category_link_version_id");
        builder.HasIndex(x => x.CategoryId).HasDatabaseName("ix_question_category_link_category_id");
    }
}
