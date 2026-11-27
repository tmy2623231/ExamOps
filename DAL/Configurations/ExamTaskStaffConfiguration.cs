using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class ExamTaskStaffConfiguration : IEntityTypeConfiguration<ExamTaskStaff>
{
    public void Configure(EntityTypeBuilder<ExamTaskStaff> builder)
    {
        builder.ToTable("exam_task_staff");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.TaskId).HasColumnName("task_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.StaffRole).HasColumnName("staff_role").IsRequired();
        builder.Property(x => x.AssignedBy).HasColumnName("assigned_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.HasOne<ExamTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AssignedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.TaskId, x.UserId, x.StaffRole }).IsUnique()
            .HasDatabaseName("ux_exam_task_staff");
        builder.HasIndex(x => x.TaskId).HasDatabaseName("ix_exam_task_staff_task_id");
        builder.HasIndex(x => new { x.UserId, x.StaffRole }).HasDatabaseName("ix_exam_task_staff_user_id");
        builder.HasIndex(x => x.AssignedBy).HasDatabaseName("ix_exam_task_staff_assigned_by");
        builder.ToTable(t =>
            t.HasCheckConstraint("ck_exam_task_staff_role", "staff_role IN ('ExamManager','Grader','Invigilator')"));
    }
}
