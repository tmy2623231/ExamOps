using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class ScoreAdjustmentConfiguration : IEntityTypeConfiguration<ScoreAdjustment>
{
    public void Configure(EntityTypeBuilder<ScoreAdjustment> builder)
    {
        // Append-only grading fact. Updates and deletes are rejected by the SQL trigger.
        builder.ToTable("score_adjustment");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.AttemptId).HasColumnName("attempt_id");
        builder.Property(x => x.OldScore).HasColumnName("old_score").HasPrecision(6, 2);
        builder.Property(x => x.NewScore).HasColumnName("new_score").HasPrecision(6, 2);
        builder.Property(x => x.Reason).HasColumnName("reason").IsRequired();
        builder.Property(x => x.AdjustedBy).HasColumnName("adjusted_by");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.HasOne<ExamAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AdjustedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.AttemptId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_score_adjustment_attempt_created").IsDescending(false, true, true);
        builder.HasIndex(x => x.AdjustedBy).HasDatabaseName("ix_score_adjustment_adjusted_by");
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_score_adjustment_idempotency");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_score_adjustment_range",
                "old_score >= 0 AND new_score >= 0 AND old_score <= 9999.99 AND new_score <= 9999.99");
            t.HasCheckConstraint("ck_score_adjustment_reason", "char_length(reason) >= 10");
        });
    }
}
