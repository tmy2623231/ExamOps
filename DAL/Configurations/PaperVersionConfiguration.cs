using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class PaperVersionConfiguration : IEntityTypeConfiguration<PaperVersion>
{
    public void Configure(EntityTypeBuilder<PaperVersion> builder)
    {
        builder.ToTable("paper_version");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.PaperId).HasColumnName("paper_id");
        builder.Property(x => x.VersionNo).HasColumnName("version_no");
        builder.Property(x => x.Title).HasColumnName("title").IsRequired();
        builder.Property(x => x.TotalScore).HasColumnName("total_score").HasPrecision(6, 2).HasDefaultValue(0);
        builder.Property(x => x.DurationMinutes).HasColumnName("duration_minutes").HasDefaultValue(60);
        builder.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Draft").IsRequired();
        builder.Property(x => x.PublishedAt).HasColumnName("published_at");
        builder.Property(x => x.PublishedBy).HasColumnName("published_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne<Paper>().WithMany().HasForeignKey(x => x.PaperId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.PublishedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.PaperId, x.VersionNo }).IsUnique().HasDatabaseName("uq_paper_version_no");
        builder.HasIndex(x => new { x.PaperId, x.Id }).IsUnique().HasDatabaseName("uq_paper_version_owner");
        builder.HasIndex(x => new { x.PaperId, x.VersionNo }).HasDatabaseName("ix_paper_version_paper_id").IsDescending(false, true);
        builder.HasIndex(x => x.PublishedBy).HasDatabaseName("ix_paper_version_published_by");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_paper_version_status_values", "status IN ('Draft','Published','Archived')");
            t.HasCheckConstraint("ck_paper_version_duration", "duration_minutes > 0 AND duration_minutes <= 1440");
            t.HasCheckConstraint("ck_paper_version_total_score", "total_score >= 0 AND total_score <= 9999.99");
        });
    }
}
