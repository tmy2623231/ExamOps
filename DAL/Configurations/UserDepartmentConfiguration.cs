using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class UserDepartmentConfiguration : IEntityTypeConfiguration<UserDepartment>
{
    public void Configure(EntityTypeBuilder<UserDepartment> builder)
    {
        builder.ToTable("user_department");
        builder.HasKey(x => new { x.UserId, x.DepartmentId });
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.DepartmentId).HasColumnName("department_id");
        builder.Property(x => x.IsPrimary).HasColumnName("is_primary");
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.DepartmentId).HasDatabaseName("ix_user_department_department_id");
        builder.HasIndex(x => x.UserId).IsUnique().HasDatabaseName("ux_user_department_one_primary")
            .HasFilter("is_primary");
    }
}
