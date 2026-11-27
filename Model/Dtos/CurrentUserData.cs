namespace ExamOps.Model.Dtos;

public sealed record CurrentUserData(
    long Id,
    string Username,
    string DisplayName,
    string Status,
    long RowVersion,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<RoleAssignmentData> Roles,
    IReadOnlyList<DepartmentAccessData> Departments,
    bool MustChangePassword);

public sealed record RoleAssignmentData(string Code, long? DepartmentId, string ScopeType, IReadOnlyList<string> Permissions);
public sealed record DepartmentAccessData(long Id, string Path, bool IsPrimary);
