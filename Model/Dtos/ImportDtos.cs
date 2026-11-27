namespace ExamOps.Model.Dtos;

public sealed record QuestionImportQuery(long? BankId, string? Status, int PageIndex, int PageSize);

/// <summary>Validated content for one new imported question; never updates an existing external code.</summary>
public sealed record ImportQuestionData(
    string? ExternalCode,
    string Type,
    string Stem,
    string Difficulty,
    decimal DefaultScore,
    string? Analysis,
    string AnswerJson,
    IReadOnlyList<QuestionOptionInput> Options,
    IReadOnlyList<string> TagNames,
    IReadOnlyList<long> CategoryIds);

public sealed record ImportRowOutcome(long RowId, long? QuestionId, string? Error);
