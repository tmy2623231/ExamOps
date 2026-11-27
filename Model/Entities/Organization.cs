namespace ExamOps.Model.Entities;

public sealed class Role
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public string ScopeType { get; set; } = "DepartmentSubtree";
    public ICollection<RolePermission> RolePermissions { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public long RowVersion { get; set; }
}

public sealed class Permission
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Module { get; set; } = "";
}

public sealed class UserRole
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public long RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public long? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class RolePermission
{
    public long RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public long PermissionId { get; set; }
    public Permission Permission { get; set; } = null!;
}

public sealed class UserDepartment
{
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public long DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public bool IsPrimary { get; set; }
}
