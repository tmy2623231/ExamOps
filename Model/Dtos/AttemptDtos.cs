namespace ExamOps.Model.Dtos;

public sealed record MyExamRow(
    long AssignmentId, long TaskId, string TaskTitle, string TaskCode, string Status,
    DateTime AvailableFrom, DateTime AvailableTo, int DurationMinutes, int AttemptLimit,
    int AttemptsUsed, int InheritedAttemptsUsed, int GrantedAttempts, long? CurrentAttemptId,
    string? CurrentAttemptStatus, DateTime? DeadlineAt, bool CanStart, string? StatusMessage, long RowVersion);

public sealed record StartExamInput(long AssignmentRowVersion);
public sealed record AttemptOptionDto(string Label, string Text);
public sealed record AttemptQuestionDto(
    long PaperQuestionId, long QuestionVersionId, string Type, string Stem,
    IReadOnlyList<AttemptOptionDto> Options, decimal Score, int SortOrder, string? AnswerJson);
public sealed record AttemptSectionDto(long SectionId, string Title, IReadOnlyList<AttemptQuestionDto> Questions);
public sealed record AttemptPaperDto(long PaperVersionId, string AlgorithmVersion, IReadOnlyList<AttemptSectionDto> Sections);
public sealed record StartAttemptResult(
    long AttemptId, long AssignmentId, int AttemptNo, long RowVersion, string StartKey,
    DateTime StartedAt, DateTime DeadlineAt, DateTime ServerNow, AttemptPaperDto Paper);
public sealed record AttemptDetail(
    long Id, long AssignmentId, long TaskId, string TaskTitle, int AttemptNo, string Status,
    long RowVersion, string? SubmitKey, DateTime StartedAt, DateTime DeadlineAt, DateTime? SubmittedAt,
    decimal MaxScore, decimal? AutoScore, decimal? Score, bool IsLate, AttemptPaperDto Paper,
    DateTime ServerNow, bool CanSave, bool CanSubmit);
public sealed record SaveAnswerInput(long RowVersion, string? AnswerJson);
public sealed class SaveAnswersInput
{
    public long RowVersion { get; set; }
    public IReadOnlyList<SaveAnswerItemInput> Answers { get; set; } = [];
}
public sealed record SaveAnswerItemInput(long QuestionVersionId, string? AnswerJson);
public sealed record SubmitAttemptInput(long RowVersion);
public sealed record AttemptSubmitResult(long AttemptId, string Status, decimal? Score, decimal MaxScore, DateTime SubmittedAt, bool Replayed);
public sealed record AttemptResultDto(
    long AttemptId, long TaskId, string TaskTitle, int AttemptNo, string Status, string ResultStatus,
    decimal? Score, decimal MaxScore, bool IsPublished, DateTime? SubmittedAt, DateTime? GradedAt,
    long? FinalAttemptId = null, int? FinalAttemptNo = null, int? ReleaseNo = null);
public sealed record AttemptEventRow(
    long Id, string EventType, string PayloadJson, DateTime OccurredAt, long? ActorId, string? ActorName);
public sealed record VoidAttemptInput(long RowVersion, string Reason);
