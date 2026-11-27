namespace ExamOps.Model.Enums;

public enum DataScope
{
    Self = 0,
    Department = 1,
    DepartmentSubtree = 2,
    Assigned = 3,
    All = 4
}

public static class DataScopeRules
{
    public static bool IsValidScopeType(string? scopeType)
        => scopeType is "All" or "Department" or "DepartmentSubtree" or "Assigned" or "Self";

    public static bool RequiresDepartmentAnchor(string? scopeType)
        => scopeType is "Department" or "DepartmentSubtree";

    public static bool MatchesDepartment(string? scopeType, long targetId, string targetPath,
        long anchorId, string anchorPath)
        => scopeType switch
        {
            "Department" => targetId == anchorId,
            "DepartmentSubtree" => targetPath.StartsWith(anchorPath, StringComparison.Ordinal),
            _ => false
        };
}
