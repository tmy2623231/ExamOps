using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class PaperQuestionConfiguration : IEntityTypeConfiguration<PaperQuestion>
{
    public void Configure(EntityTypeBuilder<PaperQuestion> builder)
    {
        builder.ToTable("paper_question");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.PaperVersionId).HasColumnName("paper_version_id");
        builder.Property(x => x.SectionId).HasColumnName("section_id");
        builder.Property(x => x.QuestionVersionId).HasColumnName("question_version_id");
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").HasDefaultValue(1);
        builder.Property(x => x.Score).HasColumnName("score").HasPrecision(6, 2).HasDefaultValue(1);
        builder.Property(x => x.IsPool).HasColumnName("is_pool").HasDefaultValue(false);
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<PaperSection>().WithMany().HasForeignKey(x => new { x.PaperVersionId, x.SectionId })
            .HasPrincipalKey(x => new { x.PaperVersionId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<QuestionVersion>().WithMany().HasForeignKey(x => x.QuestionVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.PaperVersionId, x.SectionId, x.SortOrder }).IsUnique().HasDatabaseName("uq_paper_question_order");
        builder.HasIndex(x => new { x.PaperVersionId, x.SectionId, x.QuestionVersionId }).IsUnique().HasDatabaseName("uq_paper_question_member");
        builder.HasIndex(x => new { x.PaperVersionId, x.SectionId, x.SortOrder }).HasDatabaseName("ix_paper_question_version_id");
        builder.HasIndex(x => x.SectionId).HasDatabaseName("ix_paper_question_section_id");
        builder.HasIndex(x => x.QuestionVersionId).HasDatabaseName("ix_paper_question_question_version_id");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_paper_question_sort", "sort_order > 0");
            t.HasCheckConstraint("ck_paper_question_score", "score > 0 AND score <= 9999.99");
        });
    }
}
