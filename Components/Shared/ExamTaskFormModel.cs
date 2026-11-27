namespace ExamOps.Components.Shared;

public sealed class ExamTaskFormModel
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public long DepartmentId { get; set; }
    public string? ExamDate { get; set; }
    public string? PaperVersionId { get; set; }
    public string AvailableFrom { get; set; } = "";
    public string AvailableTo { get; set; } = "";
    public string DurationText { get; set; } = "60";
    public string AttemptLimitText { get; set; } = "1";
    public string ScorePolicy { get; set; } = "Latest";
    public string RetakeApproval { get; set; } = "None";
    public string CooldownText { get; set; } = "0";
    public string? PassScoreText { get; set; }
    public string ResultReleasePolicy { get; set; } = "AfterClose";
    public string LateScorePolicy { get; set; } = "Score";
}
