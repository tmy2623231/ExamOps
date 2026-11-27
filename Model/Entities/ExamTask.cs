namespace ExamOps.Model.Entities;

public sealed class ExamTask
{
    public long Id { get; set; }
    public long DepartmentId { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateOnly? ExamDate { get; set; }
    public string Status { get; set; } = "Draft";
    public long? PaperVersionId { get; set; }
    public DateTime AvailableFrom { get; set; }
    public DateTime AvailableTo { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public int AttemptLimit { get; set; } = 1;
    public string ScorePolicy { get; set; } = "Latest";
    public string RetakeApproval { get; set; } = "None";
    public int RetakeCooldownMinutes { get; set; }
    public DateTime? RetakeDeadlineAt { get; set; }
    public decimal? PassScore { get; set; }
    public string ResultReleasePolicy { get; set; } = "AfterClose";
    public string ResultStatus { get; set; } = "Unpublished";
    public long? ResultReleaseId { get; set; }
    public DateTime? ResultPublishedAt { get; set; }
    public DateTime? ResultWithdrawnAt { get; set; }
    public string? ResultWithdrawReason { get; set; }
    public string LateScorePolicy { get; set; } = "Score";
    public long CreatedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}

public sealed class ExamTaskStaff
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public long UserId { get; set; }
    public string StaffRole { get; set; } = "ExamManager";
    public long AssignedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
