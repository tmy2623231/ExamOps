namespace ExamOps.Model.Dtos;

public sealed record PaperQuery(string? Keyword, long? DepartmentId, string? Status, int PageIndex, int PageSize);

public sealed record PaperRow(
    long Id, string Title, long DepartmentId, string DepartmentName, string Status,
    long? CurrentVersionId, int VersionNo, decimal TotalScore, int DurationMinutes,
    int SectionCount, int QuestionCount, DateTime? UpdatedAt, long RowVersion);

public sealed record PaperVersionRow(
    long Id, int VersionNo, string Title, decimal TotalScore, int DurationMinutes,
    string Status, DateTime? PublishedAt, bool IsCurrent, long RowVersion);

public sealed record PaperSectionInput(
    long Id, string Title, int SortOrder, string SelectionMode, int? PickCount,
    decimal ScorePerQuestion, long? BankId, long? CategoryId, string? QuestionType,
    string? Difficulty, IReadOnlyList<long> QuestionVersionIds);

public sealed record PaperSectionRow(
    long Id, string Title, int SortOrder, string SelectionMode, int? PickCount,
    decimal ScorePerQuestion, long? BankId, long? CategoryId, string? QuestionType,
    string? Difficulty, int QuestionCount, long RowVersion);

public sealed record PaperQuestionRow(
    long Id, long SectionId, long QuestionVersionId, long QuestionId, long BankId,
    string BankName, string? ExternalCode, string Type, string Stem, string Difficulty,
    int SortOrder, decimal Score, bool IsPool);

public sealed record PaperBlueprintIssue(long SectionId, string Code, string Message);

public sealed record PaperPreview(
    decimal TotalScore, int FixedQuestionCount, int RandomPoolCount, int SelectedRandomCount,
    int DuplicateQuestionCount, IReadOnlyList<PaperBlueprintIssue> Issues,
    IReadOnlyList<PaperTypeSummary> TypeSummary);

public sealed record PaperTypeSummary(string Type, int Count, decimal Score);

public sealed record PaperDetail(
    long Id, string Title, long DepartmentId, string DepartmentName, string Status,
    long RowVersion, long? CurrentVersionId, long VersionId, int VersionNo,
    string VersionTitle, decimal TotalScore, int DurationMinutes, string VersionStatus,
    long VersionRowVersion, DateTime? PublishedAt, IReadOnlyList<PaperVersionRow> Versions,
    long DraftVersionId, IReadOnlyList<PaperSectionRow> Sections,
    IReadOnlyList<PaperQuestionRow> Questions, PaperPreview Preview);

public sealed record PaperCandidateRow(
    long QuestionVersionId, long QuestionId, long BankId, string BankName,
    string? ExternalCode, string Type, string Stem, string Difficulty, decimal DefaultScore);

public sealed record PaperCandidateQuery(
    long? BankId, long? CategoryId, string? QuestionType, string? Difficulty,
    string? Keyword, int PageIndex, int PageSize);

public sealed record CreatePaperInput(string Title, long DepartmentId, int DurationMinutes);
public sealed record PaperVersionInput(string Title, int DurationMinutes, IReadOnlyList<PaperSectionInput> Sections);
public sealed record PaperPublishInput(long VersionId, long RowVersion);
public sealed record PaperActionInput(long RowVersion);
public sealed record PaperMutationResult(long PaperId, long VersionId);
public sealed record ApiAuditMetadata(string? IdempotencyKey, string? RequestHash, string HttpMethod, string RouteTemplate);
public sealed record PaperUpdateApiInput(long RowVersion, long VersionRowVersion, string Title, int DurationMinutes,
    IReadOnlyList<PaperSectionInput> Sections);
public sealed record PaperCopyApiInput(long RowVersion);
public sealed record PaperAddQuestionApiInput(long SectionId, long QuestionVersionId, long RowVersion, long VersionRowVersion);
public sealed record PaperDeleteQuestionApiInput(long RowVersion, long VersionRowVersion);
public sealed record PaperSectionReplaceApiInput(long RowVersion, long VersionRowVersion, IReadOnlyList<PaperSectionInput> Sections);
public sealed record PaperPublishApiInput(long VersionId, long RowVersion, long VersionRowVersion);

public static class PaperPreviewBuilder
{
    public static PaperPreview Build(IReadOnlyList<PaperSectionRow> sections, IReadOnlyList<PaperQuestionRow> questions)
    {
        var issues = new List<PaperBlueprintIssue>();
        if (sections.Count == 0) issues.Add(new(0, "SECTIONS_REQUIRED", "至少添加一个小节。"));
        var fixedQuestions = questions.Where(x => !x.IsPool).ToList();
        var fixedCounts = fixedQuestions.GroupBy(x => x.SectionId).ToDictionary(x => x.Key, x => x.Count());
        var poolCounts = questions.Where(x => x.IsPool).GroupBy(x => x.SectionId).ToDictionary(x => x.Key, x => x.Count());
        var randomCount = 0;
        decimal total = 0;
        var summaries = fixedQuestions.GroupBy(x => x.Type).Select(x => new PaperTypeSummary(x.Key, x.Count(), x.Sum(q => q.Score))).ToDictionary(x => x.Type);
        var randomSummaryCount = 0;
        decimal randomSummaryScore = 0;
        foreach (var section in sections)
        {
            if (section.ScorePerQuestion <= 0) issues.Add(new(section.Id, "SCORE_INVALID", $"{section.Title} 的每题分值必须大于 0。"));
            if (section.SelectionMode == "Fixed")
            {
                total += fixedQuestions.Where(x => x.SectionId == section.Id).Sum(x => x.Score);
                if (fixedCounts.GetValueOrDefault(section.Id) == 0) issues.Add(new(section.Id, "FIXED_EMPTY", $"{section.Title} 还没有题目。"));
            }
            else
            {
                var pick = Math.Max(0, section.PickCount ?? 0);
                var pool = poolCounts.GetValueOrDefault(section.Id);
                randomCount += pick;
                total += pick * section.ScorePerQuestion;
                randomSummaryCount += pick;
                randomSummaryScore += pick * section.ScorePerQuestion;
                if (pick <= 0) issues.Add(new(section.Id, "PICK_INVALID", $"{section.Title} 的抽题数必须大于 0。"));
                if (pool < pick) issues.Add(new(section.Id, "POOL_SHORT", $"{section.Title} 候选题 {pool} 道，少于需要抽取的 {pick} 道。"));
            }
        }
        var fixedDuplicates = fixedQuestions.GroupBy(x => x.QuestionVersionId).Where(x => x.Count() > 1).ToList();
        foreach (var duplicate in fixedDuplicates)
            issues.Add(new(duplicate.First().SectionId, "FIXED_DUPLICATE", $"固定题重复 {duplicate.Count()} 次：{duplicate.First().Stem}"));
        var poolDuplicates = questions.Where(x => x.IsPool).GroupBy(x => x.QuestionVersionId).Where(x => x.Count() > 1).ToList();
        foreach (var duplicate in poolDuplicates)
            issues.Add(new(duplicate.First().SectionId, "POOL_OVERLAP", $"候选题同时出现在多个随机小节，实际抽题时将自动去重：{duplicate.First().Stem}"));
        if (randomSummaryCount > 0)
        {
            summaries["待抽取"] = new("待抽取", randomSummaryCount, randomSummaryScore);
        }
        return new PaperPreview(total, fixedQuestions.Count, poolCounts.Values.Sum(), randomCount,
            fixedDuplicates.Sum(x => x.Count() - 1), issues.OrderBy(x => x.SectionId).ThenBy(x => x.Code).ToList(),
            summaries.OrderBy(x => x.Value.Type).Select(x => x.Value).ToList());
    }
}

