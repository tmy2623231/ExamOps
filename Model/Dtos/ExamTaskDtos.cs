namespace ExamOps.Model.Dtos;

public sealed record ExamTaskQuery(string? Keyword, long? DepartmentId, string? Status, int PageIndex, int PageSize);

public sealed record ExamTaskRow(
    long Id, string Code, string Title, long DepartmentId, string DepartmentName, string Status,
    DateOnly? ExamDate, DateTime AvailableFrom, DateTime AvailableTo, int DurationMinutes,
    int AttemptLimit, string LateScorePolicy, long? PaperVersionId, string? PaperTitle,
    int AssignmentCount, int InProgressCount, DateTime? UpdatedAt, long RowVersion);

public sealed record ExamTaskDetail(
    long Id, string Code, string Title, string? Description, long DepartmentId, string DepartmentName,
    string Status, DateOnly? ExamDate, long? PaperVersionId, string? PaperTitle,
    DateTime AvailableFrom, DateTime AvailableTo, int DurationMinutes, int AttemptLimit,
    string ScorePolicy, string RetakeApproval, int RetakeCooldownMinutes, decimal? PassScore,
    string ResultReleasePolicy, string ResultStatus, string LateScorePolicy,
    int AssignmentCount, int InProgressCount, DateTime? PublishedAt, DateTime? ClosedAt,
    DateTime? RevokedAt, DateTime? ArchivedAt, DateTime CreatedAt, long RowVersion);

public sealed record CreateExamTaskInput(
    string Code, string Title, string? Description, long DepartmentId, DateOnly? ExamDate,
    long? PaperVersionId, DateTime AvailableFrom, DateTime AvailableTo, int DurationMinutes,
    int AttemptLimit, string ScorePolicy, string RetakeApproval, int RetakeCooldownMinutes,
    decimal? PassScore, string ResultReleasePolicy, string LateScorePolicy);

public sealed record UpdateExamTaskInput(
    long RowVersion, string Title, string? Description, DateOnly? ExamDate, long? PaperVersionId,
    DateTime AvailableFrom, DateTime AvailableTo, int DurationMinutes, int AttemptLimit,
    string ScorePolicy, string RetakeApproval, int RetakeCooldownMinutes, decimal? PassScore,
    string ResultReleasePolicy, string LateScorePolicy);

public sealed record ExamTaskActionInput(long RowVersion);
public sealed record RevokeExamTaskInput(long RowVersion, string Reason, string ConfirmationTitle, string InProgressPolicy);
public sealed record ExamTaskMutationResult(long TaskId);
public sealed record ExamTaskStaffInput(long UserId, string StaffRole);
public sealed record ExamTaskStaffRow(long Id, long TaskId, long UserId, string DisplayName, string StaffRole, DateTime CreatedAt);
