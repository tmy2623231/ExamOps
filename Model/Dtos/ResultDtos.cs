namespace ExamOps.Model.Dtos;

public sealed record PublishResultInput(long RowVersion);
public sealed record WithdrawResultInput(long RowVersion, string Reason);
public sealed record PublishResultOutput(long ReleaseId, int ReleaseNo, string PayloadSha256, DateTime PublishedAt, long RowVersion);
public sealed record WithdrawResultOutput(long ReleaseId, DateTime WithdrawnAt, long RowVersion);
public sealed record ResultReleaseHistoryRow(
    long ReleaseId, int ReleaseNo, string ScorePolicy, string ResultReleasePolicy,
    string PayloadSha256, DateTime PublishedAt, long? PublishedBy, string? PublishedByName,
    int AssignmentCount, int ScoredCount);
public sealed record ResultPublicationTaskRow(
    long Id, string Title, long DepartmentId, string Status, string ResultStatus,
    string ScorePolicy, string ResultReleasePolicy, long RowVersion, long? ResultReleaseId,
    DateTime? ResultPublishedAt, DateTime? ResultWithdrawnAt, string? ResultWithdrawReason);
