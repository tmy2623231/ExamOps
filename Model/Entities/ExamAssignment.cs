namespace ExamOps.Model.Entities;

public sealed class AssignmentBatch
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string TargetJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}

public sealed class AssignmentBatchRow
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public int RowNo { get; set; }
    public long? UserId { get; set; }
    public string TargetJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public long? AssignmentId { get; set; }
    public string ErrorJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}

public sealed class ExamAssignment
{
    public long Id { get; set; }
    public long? BatchId { get; set; }
    public long TaskId { get; set; }
    public long UserId { get; set; }
    public long DepartmentIdSnapshot { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime AvailableFrom { get; set; }
    public DateTime AvailableTo { get; set; }
    public int? AttemptLimitOverride { get; set; }
    public int? DurationMinutesOverride { get; set; }
    public int AttemptsUsed { get; set; }
    public int InheritedAttemptsUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public long? ReissuedFromId { get; set; }
    public long RowVersion { get; set; } = 1;
}

public sealed class RetakeGrant
{
    public long Id { get; set; }
    public long AssignmentId { get; set; }
    public int ExtraAttempts { get; set; }
    public string Reason { get; set; } = "";
    public long GrantedBy { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class RevocationRecord
{
    public long Id { get; set; }
    public string TargetType { get; set; } = "";
    public long TargetId { get; set; }
    public string Scope { get; set; } = "KeepScore";
    public string Reason { get; set; } = "";
    public string ImpactJson { get; set; } = "{}";
    public long? ActorId { get; set; }
    public string ActorRole { get; set; } = "User";
    public string IdempotencyKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
