using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class PaperSectionConfiguration : IEntityTypeConfiguration<PaperSection>
{
    public void Configure(EntityTypeBuilder<PaperSection> builder)
    {
        builder.ToTable("paper_section");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.PaperVersionId).HasColumnName("paper_version_id");
        builder.Property(x => x.Title).HasColumnName("title").IsRequired();
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").HasDefaultValue(1);
        builder.Property(x => x.SelectionMode).HasColumnName("selection_mode").HasDefaultValue("Fixed").IsRequired();
        builder.Property(x => x.PickCount).HasColumnName("pick_count");
        builder.Property(x => x.ScorePerQuestion).HasColumnName("score_per_question").HasPrecision(6, 2).HasDefaultValue(1);
        builder.Property(x => x.BankId).HasColumnName("bank_id");
        builder.Property(x => x.CategoryId).HasColumnName("category_id");
        builder.Property(x => x.QuestionType).HasColumnName("question_type");
        builder.Property(x => x.Difficulty).HasColumnName("difficulty");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<PaperVersion>().WithMany().HasForeignKey(x => x.PaperVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<QuestionBank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<QuestionCategory>().WithMany()
            .HasForeignKey(x => new { x.BankId, x.CategoryId })
            .HasPrincipalKey(x => new { x.BankId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.PaperVersionId, x.SortOrder }).IsUnique().HasDatabaseName("uq_paper_section_order");
        builder.HasIndex(x => new { x.PaperVersionId, x.Id }).IsUnique().HasDatabaseName("uq_paper_section_owner");
        builder.HasIndex(x => new { x.PaperVersionId, x.SortOrder }).HasDatabaseName("ix_paper_section_version_id");
        builder.HasIndex(x => x.BankId).HasDatabaseName("ix_paper_section_bank_id");
        builder.HasIndex(x => x.CategoryId).HasDatabaseName("ix_paper_section_category_id");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_paper_section_mode", "selection_mode IN ('Fixed','Random')");
            t.HasCheckConstraint("ck_paper_section_sort", "sort_order > 0");
            t.HasCheckConstraint("ck_paper_section_pick_count", "(selection_mode = 'Fixed' AND pick_count IS NULL) OR (selection_mode = 'Random' AND pick_count > 0)");
            t.HasCheckConstraint("ck_paper_section_score", "score_per_question > 0 AND score_per_question <= 9999.99");
            t.HasCheckConstraint("ck_paper_section_question_type", "question_type IS NULL OR question_type IN ('SingleChoice','MultipleChoice','TrueFalse')");
            t.HasCheckConstraint("ck_paper_section_difficulty", "difficulty IS NULL OR difficulty IN ('Easy','Medium','Hard')");
            t.HasCheckConstraint("ck_paper_section_rule_mode", "selection_mode = 'Random' OR (bank_id IS NULL AND category_id IS NULL AND question_type IS NULL AND difficulty IS NULL)");
            t.HasCheckConstraint("ck_paper_section_bank_required", "selection_mode <> 'Random' OR bank_id IS NOT NULL");
        });
    }
}
