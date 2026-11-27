namespace ExamOps.Model.Entities;

public sealed class AppUser
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; } = true;
    public string Status { get; set; } = "Active";
    public long? PrimaryDepartmentId { get; set; }
    public Department? PrimaryDepartment { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}
