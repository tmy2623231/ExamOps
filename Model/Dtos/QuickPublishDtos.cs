namespace ExamOps.Model.Dtos;

public sealed record QuickBankSummary(
    long BankId,
    string BankName,
    long DepartmentId,
    string DepartmentName,
    int SingleCount,
    int MultipleCount,
    int TrueFalseCount,
    int DraftCount);

public sealed record QuickPublishRequest(
    long DepartmentId,
    long BankId,
    string ExamTitle,
    string? Description,
    string SelectionMode, // "AllFixed" or "RandomByType"
    bool IncludeSingle,
    int SingleCount,
    decimal SingleScore,
    bool IncludeMultiple,
    int MultipleCount,
    decimal MultipleScore,
    bool IncludeTrueFalse,
    int TrueFalseCount,
    decimal TrueFalseScore,
    DateTime AvailableFrom,
    DateTime AvailableTo,
    int DurationMinutes,
    int AttemptLimit,
    decimal? PassScore,
    string ResultReleasePolicy,
    bool AssignWholeDepartment,
    bool IncludeDescendants,
    string? IdempotencyKey = null);

public sealed record QuickPublishResult(
    long PaperId,
    long PaperVersionId,
    long TaskId,
    string TaskCode,
    long? BatchId,
    int TotalQuestionCount,
    decimal TotalScore);
