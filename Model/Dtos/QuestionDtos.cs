namespace ExamOps.Model.Dtos;

public sealed record QuestionBankRow(long Id, string Name, long DepartmentId, string DepartmentName, bool IsShared, string Status, int QuestionCount, long RowVersion);

public sealed record QuestionCategoryRow(long Id, long BankId, long? ParentId, string Name, bool IsActive, long RowVersion);

public sealed record QuestionTagRow(long Id, string Name, bool IsActive);

public sealed record QuestionOptionInput(string Label, string Content, bool IsCorrect, int SortOrder);

public sealed record QuestionOptionRow(string Label, string Content, bool IsCorrect, int SortOrder);

public sealed record QuestionEditInput(
    long BankId,
    string? ExternalCode,
    string Type,
    string Stem,
    string Difficulty,
    decimal DefaultScore,
    string? Analysis,
    IReadOnlyList<QuestionOptionInput> Options,
    IReadOnlyList<string> TagNames,
    IReadOnlyList<long> CategoryIds,
    string AnswerValue,
    IReadOnlyList<string> AnswerValues,
    bool TrueFalseAnswer);

public sealed record QuestionVersionRow(long Id, int VersionNo, string Status, DateTime? PublishedAt, bool IsCurrent);

public sealed record QuestionRow(
    long Id,
    long BankId,
    string BankName,
    string? ExternalCode,
    string Type,
    string Status,
    string Stem,
    string Difficulty,
    decimal DefaultScore,
    string Tags,
    long RowVersion);

public sealed record QuestionDetail(
    long Id,
    long BankId,
    string? ExternalCode,
    string Type,
    string Status,
    long RowVersion,
    long? CurrentVersionId,
    IReadOnlyList<QuestionVersionRow> Versions,
    long DraftVersionId,
    string Stem,
    string Difficulty,
    decimal DefaultScore,
    string? Analysis,
    IReadOnlyList<QuestionOptionRow> Options,
    string AnswerValue,
    IReadOnlyList<string> AnswerValues,
    bool TrueFalseAnswer,
    IReadOnlyList<string> TagNames,
    IReadOnlyList<long> CategoryIds);

public sealed record QuestionQuery(string? Keyword, long? BankId, string? Type, string? Status, long? TagId, int PageIndex, int PageSize, long? CategoryId = null);

public sealed record QuestionImportPreview(
    long BatchId,
    string Status,
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    int WarningRows,
    int DuplicateRows,
    int ImportedRows,
    int FailedRows,
    int ExcludedRows,
    long RowVersion,
    bool Replayed);

public sealed record QuestionImportRowRow(int RowNo, string? ExternalCode, string Status, string? Stem, string? AnswerSummary, string Errors, string Warnings);

public sealed record QuestionImportBatchDetail(
    QuestionImportPreview Preview,
    long BankId,
    string FileName,
    string Mode,
    DateTime CreatedAt,
    DateTime? ConfirmedAt,
    DateTime? FinishedAt,
    IReadOnlyList<QuestionImportRowRow> Rows,
    int TotalCount,
    int PageIndex,
    int PageSize);

public sealed record CreateQuestionResult(long QuestionId, long DraftVersionId);

public sealed record ImportConfirmInput(long BatchId, string Mode, long RowVersion, IReadOnlyList<int> ExcludeWarningRowNos);
