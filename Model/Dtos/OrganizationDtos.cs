namespace ExamOps.Model.Dtos;

public sealed record DepartmentRow(
    long Id,
    long? ParentId,
    string Name,
    string Path,
    int SortOrder,
    bool IsActive,
    int ChildCount,
    int UserCount,
    long RowVersion);

public sealed record UserRow(
    long Id,
    string Username,
    string DisplayName,
    string? Email,
    string Status,
    long? PrimaryDepartmentId,
    string? PrimaryDepartmentName,
    string Roles,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    long RowVersion);

public sealed record UserDetail(
    long Id,
    string Username,
    string DisplayName,
    string? Email,
    string Status,
    long? PrimaryDepartmentId,
    bool MustChangePassword,
    long RowVersion,
    IReadOnlyList<RoleAssignmentDto> Roles,
    IReadOnlyList<long> DepartmentIds);

public sealed record RoleAssignmentDto(long RoleId, long? DepartmentId);

public sealed record RoleRow(long Id, string Code, string Name, bool IsSystem, bool IsActive, string ScopeType,
    int UserCount, IReadOnlyList<string> Permissions, long RowVersion);
public sealed record PermissionRow(long Id, string Code, string Name, string Module);

public sealed record OrganizationSummary(int DepartmentCount, int UserCount, int ActiveUserCount, int RoleCount);
