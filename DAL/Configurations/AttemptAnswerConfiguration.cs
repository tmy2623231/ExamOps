using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class AttemptAnswerConfiguration : IEntityTypeConfiguration<AttemptAnswer>
{
    public void Configure(EntityTypeBuilder<AttemptAnswer> builder)
    {
        builder.ToTable("attempt_answer");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.AttemptId).HasColumnName("attempt_id");
        builder.Property(x => x.QuestionVersionId).HasColumnName("question_version_id");
        builder.Property(x => x.AnswerJson).HasColumnName("answer_json").HasColumnType("jsonb");
        builder.Property(x => x.Score).HasColumnName("score").HasPrecision(6, 2).HasDefaultValue(0);
        builder.Property(x => x.MaxScore).HasColumnName("max_score").HasPrecision(6, 2).HasDefaultValue(0);
        builder.Property(x => x.GradingStatus).HasColumnName("grading_status").HasDefaultValue("Pending").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<ExamAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<QuestionVersion>().WithMany().HasForeignKey(x => x.QuestionVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.AttemptId, x.QuestionVersionId }).IsUnique()
            .HasDatabaseName("uq_attempt_answer_question");
        builder.HasIndex(x => x.AttemptId).HasDatabaseName("ix_attempt_answer_attempt_id");
        builder.HasIndex(x => x.QuestionVersionId).HasDatabaseName("ix_attempt_answer_question_version_id");
        builder.HasIndex(x => new { x.GradingStatus, x.AttemptId }).HasDatabaseName("ix_attempt_answer_anomaly")
            .HasFilter("grading_status = 'Invalid'");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_attempt_answer_score", "score >= 0 AND max_score >= 0 AND score <= max_score AND max_score <= 9999.99");
            t.HasCheckConstraint("ck_attempt_answer_objective_score",
                "(grading_status = 'Pending' AND score = 0) OR " +
                "(grading_status = 'Graded' AND score IN (0, max_score)) OR " +
                "(grading_status = 'Invalid' AND score = 0)");
            t.HasCheckConstraint("ck_attempt_answer_grading_status_values", "grading_status IN ('Pending','Graded','Invalid')");
        });
    }
}
