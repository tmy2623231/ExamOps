namespace ExamOps.Model.Entities;

public sealed class ExamAttempt
{
    public long Id { get; set; }
    public long AssignmentId { get; set; }
    public int AttemptNo { get; set; }
    public string Status { get; set; } = "InProgress";
    public string StartKey { get; set; } = "";
    public string? SubmitKey { get; set; }
    public string PaperSnapshotJson { get; set; } = "{}";
    public DateTime StartedAt { get; set; }
    public DateTime DeadlineAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public decimal? AutoScore { get; set; }
    public decimal? Score { get; set; }
    public decimal MaxScore { get; set; }
    public DateTime? GradedAt { get; set; }
    public long? GradedBy { get; set; }
    public bool IsLate { get; set; }
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public long RowVersion { get; set; } = 1;
}

public sealed class AttemptAnswer
{
    public long Id { get; set; }
    public long AttemptId { get; set; }
    public long QuestionVersionId { get; set; }
    public string? AnswerJson { get; set; }
    public decimal Score { get; set; }
    public decimal MaxScore { get; set; }
    public string GradingStatus { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}

public sealed class AttemptEvent
{
    public long Id { get; set; }
    public long AttemptId { get; set; }
    public long? ActorId { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public DateTime? ClientAt { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string EventKey { get; set; } = "";
}
