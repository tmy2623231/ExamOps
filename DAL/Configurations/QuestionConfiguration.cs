using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    public void Configure(EntityTypeBuilder<Question> builder)
    {
        builder.ToTable("question");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.BankId).HasColumnName("bank_id");
        builder.Property(x => x.ExternalCode).HasColumnName("external_code").HasMaxLength(128);
        builder.Property(x => x.Type).HasColumnName("type").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Draft").IsRequired();
        builder.Property(x => x.CurrentVersionId).HasColumnName("current_version_id");
        builder.Property(x => x.ArchivedAt).HasColumnName("archived_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<QuestionBank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        // SQL 的复合外键 (id,current_version_id) 还负责校验版本归属；EF 仅映射版本主键，
        // 避免把主键 Id 同时当成外键后误判为“非数据库生成”。
        builder.HasOne<QuestionVersion>().WithMany().HasForeignKey(x => x.CurrentVersionId)
            .HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.BankId, x.ExternalCode }).IsUnique()
            .HasDatabaseName("ux_question_bank_external_code").HasFilter("external_code IS NOT NULL");
        builder.HasIndex(x => x.BankId).HasDatabaseName("ix_question_bank_id");
        builder.HasIndex(x => new { x.Status, x.Type }).HasDatabaseName("ix_question_status_type");
        builder.HasIndex(x => x.CurrentVersionId).HasDatabaseName("ix_question_current_version_id");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_question_type", "type IN ('SingleChoice','MultipleChoice','TrueFalse')");
            t.HasCheckConstraint("ck_question_status", "status IN ('Draft','Published','Archived')");
        });
    }
}
