using ExamOps.Model.Common;
using ExamOps.Model.Dtos;

namespace ExamOps.BLL.Validators;

public sealed record PreparedQuestion(
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
    string AnswerJson);

public static class QuestionValidator
{
    private const string Labels = "ABCDEF";

    public static Result<PreparedQuestion> Validate(QuestionEditInput input)
    {
        if (input.Options is null || input.TagNames is null || input.CategoryIds is null || input.AnswerValues is null)
            return Result<PreparedQuestion>.Fail("题目内容不完整", "INVALID_QUESTION");
        var type = input.Type?.Trim() ?? "";
        if (type is not ("SingleChoice" or "MultipleChoice" or "TrueFalse"))
            return Result<PreparedQuestion>.Fail("不支持的题型", "INVALID_TYPE");
        var difficulty = input.Difficulty?.Trim() ?? "";
        if (difficulty is not ("Easy" or "Medium" or "Hard"))
            return Result<PreparedQuestion>.Fail("难度不正确", "INVALID_DIFFICULTY");
        if (input.DefaultScore < 0.01m || input.DefaultScore > 9999.99m || decimal.Round(input.DefaultScore, 2) != input.DefaultScore)
            return Result<PreparedQuestion>.Fail("分值须在 0.01–9999.99 之间，最多两位小数", "INVALID_SCORE");
        var stem = input.Stem?.Trim() ?? "";
        if (stem.Length is 0 or > 10000)
            return Result<PreparedQuestion>.Fail("题干须为 1–10000 个字符", "INVALID_STEM");
        var code = string.IsNullOrWhiteSpace(input.ExternalCode) ? null : input.ExternalCode.Trim();
        if (code?.Length > 128) return Result<PreparedQuestion>.Fail("外部编码不能超过 128 个字符", "INVALID_EXTERNAL_CODE");

        IReadOnlyList<QuestionOptionInput> options;
        string answerValue = "";
        IReadOnlyList<string> answerValues = [];
        var trueFalseAnswer = false;
        if (type == "TrueFalse")
        {
            options =
            [
                new QuestionOptionInput("A", "正确", input.TrueFalseAnswer, 1),
                new QuestionOptionInput("B", "错误", !input.TrueFalseAnswer, 2)
            ];
            trueFalseAnswer = input.TrueFalseAnswer;
        }
        else
        {
            if (input.Options.Count is < 2 or > 6)
                return Result<PreparedQuestion>.Fail("选择题须有 2–6 个选项", "INVALID_OPTIONS");
            options = input.Options.OrderBy(x => x.SortOrder).ToArray();
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                if (option.SortOrder != i + 1 || option.Label != Labels[i].ToString() || string.IsNullOrWhiteSpace(option.Content))
                    return Result<PreparedQuestion>.Fail("选项标签须按 A–F 连续排列，顺序须从 1 连续排列，内容不能为空", "INVALID_OPTIONS");
            }
            var correct = options.Where(x => x.IsCorrect).Select(x => x.Label).ToHashSet(StringComparer.Ordinal);
            if (type == "SingleChoice")
            {
                answerValue = input.AnswerValue?.Trim().ToUpperInvariant() ?? "";
                if (correct.Count != 1 || answerValue.Length != 1 || !correct.Contains(answerValue))
                    return Result<PreparedQuestion>.Fail("单选题须恰好一个正确选项，且答案与正确选项一致", "INVALID_ANSWER");
            }
            else
            {
                answerValues = (input.AnswerValues ?? []).Select(x => x?.Trim().ToUpperInvariant() ?? "")
                    .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (correct.Count < 2 || answerValues.Count < 2 || !correct.SetEquals(answerValues))
                    return Result<PreparedQuestion>.Fail("多选题须至少两个正确选项，且答案集合与正确选项完全一致", "INVALID_ANSWER");
            }
        }
        var tagNames = input.TagNames.Select(x => x?.Trim() ?? "").Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (tagNames.Any(x => x.Length > 128)) return Result<PreparedQuestion>.Fail("标签名称不能超过 128 个字符", "INVALID_TAG");
        var categoryIds = input.CategoryIds.Distinct().ToArray();
        var answerJson = QuestionAnswerJson.Serialize(type, answerValue, answerValues, trueFalseAnswer);
        return Result<PreparedQuestion>.Ok(new(input.BankId, code, type, stem, difficulty, input.DefaultScore,
            string.IsNullOrWhiteSpace(input.Analysis) ? null : input.Analysis.Trim(), options, tagNames, categoryIds, answerJson));
    }
}
