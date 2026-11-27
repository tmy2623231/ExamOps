using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamOps.Model.Common;

namespace ExamOps.BLL;

public sealed record ExamPaperSectionSource(long Id, string Title, int SortOrder, string SelectionMode, int? PickCount, decimal ScorePerQuestion);
public sealed record ExamPaperQuestionSource(long PaperQuestionId, long SectionId, long QuestionVersionId, long QuestionId, decimal Score, bool IsPool);
public sealed record ExamSelectedQuestion(long SectionId, long PaperQuestionId, long QuestionVersionId, long QuestionId, decimal Score, int SortOrder);
public sealed record ScoredAttempt(long AttemptId, int AttemptNo, decimal Score, decimal MaxScore, decimal AutoScore, DateTime GradedAt);
public sealed record FinalScoreSelection(long? AttemptId, decimal? FinalScore, decimal? MaxScore, bool? Passed,
    IReadOnlyList<long> ValidAttemptIds, string Method);

public sealed record ExamPaperSelection(bool IsSuccess, string? Error, string AlgorithmVersion,
    string SeedHex, IReadOnlyList<ExamSelectedQuestion> Questions)
{
    public static ExamPaperSelection Fail(string error) => new(false, error, "", "", []);
}

public static class ExamAlgorithms
{
    public const string AlgorithmVersion = "sha256-kuhn-v1";

    public static ExamPaperSelection SelectPaper(byte[] seed, IReadOnlyList<ExamPaperSectionSource> sections,
        IReadOnlyList<ExamPaperQuestionSource> questions)
    {
        if (seed.Length != 32 || sections.Count == 0) return ExamPaperSelection.Fail("试卷没有可用小节或随机种子无效。");
        var seedHex = Convert.ToHexString(seed).ToLowerInvariant();
        var selected = new List<ExamSelectedQuestion>();
        var usedQuestionIds = new HashSet<long>();
        foreach (var section in sections.Where(x => x.SelectionMode == "Fixed").OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
        {
            foreach (var question in questions.Where(x => x.SectionId == section.Id && !x.IsPool).OrderBy(x => x.PaperQuestionId))
            {
                if (!usedQuestionIds.Add(question.QuestionId)) continue;
                selected.Add(new(section.Id, question.PaperQuestionId, question.QuestionVersionId, question.QuestionId,
                    question.Score, selected.Count(x => x.SectionId == section.Id) + 1));
            }
        }

        var randomSections = sections.Where(x => x.SelectionMode == "Random").OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToArray();
        var slots = new List<(ExamPaperSectionSource Section, int Index, string Hash)>();
        var candidates = new Dictionary<long, List<ExamPaperQuestionSource>>();
        foreach (var section in randomSections)
        {
            var pool = questions.Where(x => x.SectionId == section.Id && x.IsPool && !usedQuestionIds.Contains(x.QuestionId))
                .GroupBy(x => x.QuestionId)
                .Select(group => group.OrderBy(x => Hash(seedHex, section.Id, x)).First())
                .OrderBy(x => Hash(seedHex, section.Id, x)).ToArray();
            foreach (var question in pool)
            {
                if (!candidates.ContainsKey(question.QuestionId)) candidates[question.QuestionId] = [];
                candidates[question.QuestionId].Add(question);
            }
            var pick = section.PickCount ?? 0;
            for (var i = 0; i < pick; i++)
                slots.Add((section, i, Hash(seedHex, section.Id, "slot", i)));
        }

        // Deterministic augmenting paths produce a maximum matching across overlapping pools.
        var slotQuestion = new Dictionary<int, long>();
        var questionSlot = new Dictionary<long, int>();
        bool Augment(int slotIndex, HashSet<int> visitedSlots)
        {
            if (!visitedSlots.Add(slotIndex)) return false;
            var slot = slots[slotIndex];
            var edges = candidates.Keys.Where(questionId => candidates[questionId].Any(x => x.SectionId == slot.Section.Id))
                .OrderBy(questionId => Hash(seedHex, slot.Section.Id, slot.Index, questionId));
            foreach (var questionId in edges)
            {
                if (!questionSlot.TryGetValue(questionId, out var owner) || Augment(owner, visitedSlots))
                {
                    slotQuestion[slotIndex] = questionId;
                    questionSlot[questionId] = slotIndex;
                    return true;
                }
            }
            return false;
        }
        foreach (var slotIndex in Enumerable.Range(0, slots.Count).OrderBy(index => slots[index].Hash))
        {
            if (!Augment(slotIndex, []))
                return ExamPaperSelection.Fail("随机题池跨小节去重后不足，请调整候选池或抽题数。");
        }

        foreach (var group in slots.Select((slot, index) => (Slot: slot, Index: index))
                     .GroupBy(x => x.Slot.Section.Id))
        {
            var section = group.First().Slot.Section;
            var picked = group.Select(x => (Index: x.Index, QuestionId: slotQuestion[x.Index]))
                .OrderBy(x => Hash(seedHex, section.Id, x.Index, x.QuestionId)).ToArray();
            for (var i = 0; i < picked.Length; i++)
            {
                var questionId = picked[i].QuestionId;
                var question = candidates[questionId].First(x => x.SectionId == section.Id);
                selected.Add(new(section.Id, question.PaperQuestionId, question.QuestionVersionId, question.QuestionId,
                    section.ScorePerQuestion, i + 1));
            }
        }
        return new(true, null, AlgorithmVersion, seedHex,
            sections.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                .SelectMany(section => selected.Where(x => x.SectionId == section.Id)).ToArray());
    }

    public static Result<string?> ValidateAnswer(string type, string? answerJson, IReadOnlySet<string> labels)
    {
        if (string.IsNullOrWhiteSpace(answerJson) || answerJson == "{}") return Result<string?>.Ok(null);
        JsonDocument document;
        try { document = JsonDocument.Parse(answerJson); }
        catch (JsonException) { return Result<string?>.Fail("答案格式无效", "ANSWER_FORMAT_INVALID"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Result<string?>.Fail("答案格式无效", "ANSWER_FORMAT_INVALID");
            var keys = root.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (type == "SingleChoice")
            {
                if (!keys.SequenceEqual(["value"]) || root.GetProperty("value").ValueKind != JsonValueKind.String)
                    return Result<string?>.Fail("单选题答案格式无效", "ANSWER_FORMAT_INVALID");
                var value = root.GetProperty("value").GetString()!;
                return labels.Contains(value) ? Result<string?>.Ok(JsonSerializer.Serialize(new { value }))
                    : Result<string?>.Fail("单选题答案不在选项范围内", "ANSWER_FORMAT_INVALID");
            }
            if (type == "MultipleChoice")
            {
                if (!keys.SequenceEqual(["values"]) || root.GetProperty("values").ValueKind != JsonValueKind.Array)
                    return Result<string?>.Fail("多选题答案格式无效", "ANSWER_FORMAT_INVALID");
                var values = root.GetProperty("values").EnumerateArray().ToArray();
                if (values.Length == 0 || values.Any(x => x.ValueKind != JsonValueKind.String))
                    return Result<string?>.Fail("多选题答案格式无效", "ANSWER_FORMAT_INVALID");
                var labelsSelected = values.Select(x => x.GetString()!).ToArray();
                if (labelsSelected.Distinct(StringComparer.Ordinal).Count() != labelsSelected.Length || labelsSelected.Any(x => !labels.Contains(x)))
                    return Result<string?>.Fail("多选题答案包含重复或非法选项", "ANSWER_FORMAT_INVALID");
                var canonical = labelsSelected.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                return Result<string?>.Ok(JsonSerializer.Serialize(new { values = canonical }));
            }
            if (type == "TrueFalse")
            {
                if (!keys.SequenceEqual(["value"]) || root.GetProperty("value").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Result<string?>.Fail("判断题答案格式无效", "ANSWER_FORMAT_INVALID");
                return Result<string?>.Ok(JsonSerializer.Serialize(new { value = root.GetProperty("value").GetBoolean() }));
            }
            return Result<string?>.Fail("不支持的题型", "ANSWER_FORMAT_INVALID");
        }
    }

    public static decimal GradeObjective(string type, string? answerJson, string correctJson, decimal maxScore, bool zeroScore)
    {
        if (zeroScore || string.IsNullOrWhiteSpace(answerJson) || answerJson == "{}") return 0;
        var labels = new HashSet<string>(StringComparer.Ordinal) { "A", "B", "C", "D", "E", "F" };
        var normalized = ValidateAnswer(type, answerJson, labels);
        var correct = ValidateAnswer(type, correctJson, labels);
        if (!normalized.IsSuccess || normalized.Value is null || !correct.IsSuccess || correct.Value is null) return 0;
        return string.Equals(normalized.Value, correct.Value, StringComparison.Ordinal) ? maxScore : 0;
    }

    public static FinalScoreSelection CalculateFinalScore(string policy, decimal? passScore,
        IReadOnlyList<ScoredAttempt> attempts)
    {
        var valid = attempts.Where(x => x.Score >= 0 && x.Score <= x.MaxScore && x.MaxScore <= 9999.99m)
            .OrderBy(x => x.AttemptNo).ThenBy(x => x.AttemptId).ToArray();
        if (policy is not ("Latest" or "First" or "Highest" or "Average"))
            throw new InvalidOperationException("RESULT_POLICY_BLOCKED");
        if (valid.Length == 0) return new(null, null, null, null, [], policy);
        ScoredAttempt selected;
        decimal score;
        if (policy == "Average")
        {
            selected = valid[^1];
            score = decimal.Round(valid.Average(x => x.Score), 2, MidpointRounding.AwayFromZero);
        }
        else
        {
            selected = policy switch
            {
                "Latest" => valid[^1],
                "First" => valid[0],
                _ => valid.OrderByDescending(x => x.Score).ThenByDescending(x => x.AttemptNo).ThenByDescending(x => x.AttemptId).First()
            };
            score = selected.Score;
        }
        return new(selected.AttemptId, score, selected.MaxScore,
            passScore.HasValue ? score >= passScore.Value : null, valid.Select(x => x.AttemptId).ToArray(), policy);
    }

    private static string Hash(string seed, long sectionId, ExamPaperQuestionSource question)
        => Hash(seed, sectionId, question.PaperQuestionId, question.QuestionVersionId, question.QuestionId);

    private static string Hash(params object[] values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values))));

    public static void RunAlgorithmSelfCheck()
    {
        var sections = new[]
        {
            new ExamPaperSectionSource(1, "固定", 1, "Fixed", null, 5),
            new ExamPaperSectionSource(2, "随机甲", 2, "Random", 2, 5),
            new ExamPaperSectionSource(3, "随机乙", 3, "Random", 2, 5)
        };
        var questions = new[]
        {
            new ExamPaperQuestionSource(11, 1, 101, 1, 5, false),
            new ExamPaperQuestionSource(21, 2, 201, 2, 5, true),
            new ExamPaperQuestionSource(22, 2, 202, 3, 5, true),
            new ExamPaperQuestionSource(23, 2, 203, 4, 5, true),
            new ExamPaperQuestionSource(31, 3, 301, 3, 5, true),
            new ExamPaperQuestionSource(32, 3, 302, 4, 5, true),
            new ExamPaperQuestionSource(33, 3, 303, 5, 5, true)
        };
        var seed = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var first = SelectPaper(seed, sections, questions);
        var second = SelectPaper(seed, sections, questions);
        if (!first.IsSuccess || first.Questions.Count != 5 || first.Questions.Select(x => x.QuestionId).Distinct().Count() != 5
            || !first.Questions.Select(x => x.QuestionVersionId).SequenceEqual(second.Questions.Select(x => x.QuestionVersionId)))
            throw new InvalidOperationException("Phase 4 抽题可复现性或去重自查失败。");
        if (SelectPaper(seed, sections, questions.Take(5).ToArray()).IsSuccess)
            throw new InvalidOperationException("Phase 4 候选不足自查失败。");
        if (!ValidateAnswer("MultipleChoice", "{\"values\":[\"B\",\"A\"]}", new HashSet<string>(StringComparer.Ordinal) { "A", "B" }).IsSuccess
            || GradeObjective("MultipleChoice", "{\"values\":[\"A\"]}", "{\"values\":[\"A\",\"B\"]}", 5, false) != 0
            || GradeObjective("MultipleChoice", "{\"values\":[\"B\",\"A\"]}", "{\"values\":[\"B\",\"A\"]}", 5, false) != 5)
            throw new InvalidOperationException("Phase 4 答案校验或 exact-set 判分自查失败。");
    }
}
