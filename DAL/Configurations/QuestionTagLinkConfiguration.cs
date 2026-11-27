using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class QuestionTagLinkConfiguration : IEntityTypeConfiguration<QuestionTagLink>
{
    public void Configure(EntityTypeBuilder<QuestionTagLink> builder)
    {
        builder.ToTable("question_tag_link");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.QuestionVersionId).HasColumnName("question_version_id");
        builder.Property(x => x.TagId).HasColumnName("tag_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.HasOne<QuestionVersion>().WithMany().HasForeignKey(x => x.QuestionVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<QuestionTag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.QuestionVersionId, x.TagId }).IsUnique().HasDatabaseName("ux_question_tag_link_pair");
        builder.HasIndex(x => x.TagId).HasDatabaseName("ix_question_tag_link_tag_id");
        builder.HasIndex(x => x.QuestionVersionId).HasDatabaseName("ix_question_tag_link_version_id");
    }
}
