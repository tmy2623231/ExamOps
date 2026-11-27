namespace ExamOps.Model.Dtos;

public static class ReviewTypes
{
    public const string FormatAnomaly = "FormatAnomaly";
    public const string Dispute = "Dispute";
    public const string VoidRequest = "VoidRequest";
}

public sealed record ReviewQueueRow(
    long AttemptId, long AssignmentId, long TaskId, string TaskTitle, long UserId, string UserName,
    long DepartmentId, string DepartmentName, int AttemptNo, string Status, decimal? AutoScore,
    decimal? Score, decimal MaxScore, long RowVersion, DateTime? SubmittedAt, DateTime? GradedAt,
    int InvalidAnswerCount, string? LatestReviewType, DateTime? LatestReviewAt);

public sealed record ReviewEventInput(string ReviewType, string Reason);
public sealed record ReviewEventResult(long EventId, long AttemptId, string ReviewType, DateTime OccurredAt);
public sealed record AdjustScoreInput(decimal OldScore, decimal NewScore, string Reason, long RowVersion);
public sealed record AdjustScoreResult(
    long AdjustmentId, long AttemptId, decimal Score, decimal AutoScore, string Status, long RowVersion, bool Replayed = false);
public sealed record ScoreAdjustmentRow(
    long Id, long AttemptId, decimal OldScore, decimal NewScore, string Reason, long AdjustedBy,
    string AdjustedByName, DateTime CreatedAt);

