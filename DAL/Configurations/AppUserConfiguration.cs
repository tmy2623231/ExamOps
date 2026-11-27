using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamOps.DAL.Configurations;

public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("app_user");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.Username).HasColumnName("username").HasMaxLength(64).IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(x => x.Email).HasColumnName("email");
        builder.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
        builder.Property(x => x.MustChangePassword).HasColumnName("must_change_password");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        builder.Property(x => x.PrimaryDepartmentId).HasColumnName("primary_department_id");
        builder.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1).IsConcurrencyToken();
        builder.HasOne(x => x.PrimaryDepartment).WithMany().HasForeignKey(x => x.PrimaryDepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.Username).IsUnique().HasDatabaseName("ux_app_user_username");
        builder.HasIndex(x => x.PrimaryDepartmentId).HasDatabaseName("ix_app_user_primary_department_id");
        builder.HasIndex(x => new { x.Status, x.DisplayName }).HasDatabaseName("ix_app_user_status_display_name");
    }
}
