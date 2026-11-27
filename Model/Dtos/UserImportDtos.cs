namespace ExamOps.Model.Dtos;

public sealed record UserImportRowInput(
    int RowNo,
    string Username,
    string DisplayName,
    string DepartmentName,
    string RolePreset,
    string? Email,
    string? InitialPassword);

public sealed record UserImportCredentialRow(
    int RowNo,
    string Username,
    string DisplayName,
    string DepartmentName,
    string RoleLabel,
    string InitialPassword);

public sealed record UserImportRowError(int RowNo, string Username, string Message);

public sealed record UserBatchImportResult(
    int CreatedCount,
    int SkippedCount,
    IReadOnlyList<UserImportCredentialRow> CreatedCredentials,
    IReadOnlyList<UserImportRowError> Errors);
