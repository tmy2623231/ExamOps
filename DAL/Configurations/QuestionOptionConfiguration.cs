using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class QuestionOptionConfiguration : IEntityTypeConfiguration<QuestionOption>
{
    public void Configure(EntityTypeBuilder<QuestionOption> builder)
    {
        builder.ToTable("question_option");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.QuestionVersionId).HasColumnName("question_version_id");
        builder.Property(x => x.Label).HasColumnName("label").HasMaxLength(1).IsRequired();
        builder.Property(x => x.Content).HasColumnName("content").IsRequired();
        builder.Property(x => x.IsCorrect).HasColumnName("is_correct").HasDefaultValue(false);
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").HasDefaultValue(1);
        builder.HasOne<QuestionVersion>().WithMany().HasForeignKey(x => x.QuestionVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.QuestionVersionId, x.Label }).IsUnique().HasDatabaseName("uq_question_option_label");
        builder.HasIndex(x => new { x.QuestionVersionId, x.SortOrder }).IsUnique().HasDatabaseName("uq_question_option_sort");
        builder.HasIndex(x => x.QuestionVersionId).HasDatabaseName("ix_question_option_version_id");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_question_option_label", "label IN ('A','B','C','D','E','F')");
            t.HasCheckConstraint("ck_question_option_sort", "sort_order > 0");
        });
    }
}
