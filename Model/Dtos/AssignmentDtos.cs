using ExamOps.Model.Common;

namespace ExamOps.Model.Dtos;

public sealed record AssignmentTargetInput(
    IReadOnlyList<long> UserIds, IReadOnlyList<long> DepartmentIds, bool IncludeDescendants,
    bool ReissueRevoked, bool InheritAttempts);

public sealed record AssignmentPreviewRow(
    long UserId, string DisplayName, string DepartmentName, string Outcome, string? Message, long? ExistingAssignmentId);

public sealed record AssignmentPreview(
    long TaskId, long TaskRowVersion, int TotalCount, IReadOnlyList<AssignmentPreviewRow> Rows,
    int EligibleCount, int SkippedCount, int FailedCount);

public sealed record AssignExamInput(AssignmentTargetInput Target, long TaskRowVersion);
public sealed record AssignmentMutationResult(long BatchId, long TaskId);
public sealed record AssignmentBatchRowDto(
    long Id, long BatchId, int RowNo, long? UserId, string? DisplayName, string Status,
    long? AssignmentId, string ErrorJson, long RowVersion);
public sealed record AssignmentBatchDetail(
    long Id, long TaskId, string TaskTitle, string Status, int TotalCount, int SuccessCount,
    int FailedCount, int SkippedCount, DateTime? StartedAt, DateTime? FinishedAt, DateTime CreatedAt,
    long RowVersion, PagedResult<AssignmentBatchRowDto> Rows);
public sealed record ExamAssignmentRow(
    long Id, long TaskId, string TaskTitle, long UserId, string DisplayName, long DepartmentIdSnapshot,
    string DepartmentName, string Status, DateTime AvailableFrom, DateTime AvailableTo,
    int AttemptLimit, int AttemptsUsed, int InheritedAttemptsUsed, int GrantedAttempts,
    long? CurrentAttemptId, string? CurrentAttemptStatus, DateTime CreatedAt, long RowVersion);
public sealed record AssignmentQuery(long TaskId, string? Status, string? Keyword, int PageIndex, int PageSize);
public sealed record RevokeAssignmentInput(long RowVersion, string Reason, string InProgressPolicy);
public sealed record GrantRetakeInput(long RowVersion, int ExtraAttempts, string Reason, DateTime? ExpiresAt);
