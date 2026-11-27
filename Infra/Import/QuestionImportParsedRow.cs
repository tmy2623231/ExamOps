namespace ExamOps.Infra.Import;

public sealed record QuestionImportParsedRow(
    int RowNo,
    string? ExternalCode,
    string Type,
    string Stem,
    string Difficulty,
    decimal DefaultScore,
    string? Analysis,
    IReadOnlyList<ParsedQuestionOption> Options,
    string? AnswerValue,
    IReadOnlyList<string> AnswerValues,
    bool TrueFalseAnswer,
    IReadOnlyList<string> TagNames,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record ParsedQuestionOption(string Label, string Text);
