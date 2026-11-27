using ExamOps.Model.Dtos;

namespace ExamOps.Components.Shared;

public static class ExamAudit
{
    public static ApiAuditMetadata Create(string action, long targetId) => new(
        $"blazor:{action}:{targetId}:{Guid.NewGuid():N}", "blazor-server", "POST", $"/ui/exams/{action}");
}
