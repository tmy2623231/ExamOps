namespace ExamOps.Model.Dtos;

public sealed record DepartmentTemplateNode(string Name, IReadOnlyList<DepartmentTemplateNode> Children);

public sealed record DepartmentTemplatePreset(
    string Key,
    string Name,
    string Badge,
    string Description,
    IReadOnlyList<DepartmentTemplateNode> Nodes);

public sealed record ApplyDepartmentTemplateResult(int CreatedCount, int SkippedCount);
