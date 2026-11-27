namespace ExamOps.BLL;

public static class DepartmentPath
{
    public static string BuildChildPath(string parentPath, IEnumerable<int> siblingSegments)
    {
        if (parentPath.Length > 0 && !parentPath.EndsWith('.'))
            throw new ArgumentException("父部门路径格式无效。", nameof(parentPath));
        var next = (siblingSegments.DefaultIfEmpty(0).Max()) + 1;
        if (next > 999) throw new InvalidOperationException("同一父部门下最多支持 999 个子部门。");
        return $"{parentPath}{next:D3}.";
    }
}

public static class AnswerValidator
{
    public static GradeDecision Grade(string questionType, IEnumerable<string> selected, IEnumerable<string> correct)
    {
        var chosen = selected.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var answers = correct.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var error = questionType switch
        {
            "SingleChoice" when chosen.Count != 1 || answers.Count != 1 => "单选题必须且只能选择一个答案。",
            "MultipleChoice" when chosen.Count == 0 || answers.Count < 2 => "多选题答案数量不正确。",
            "TrueFalse" when chosen.Count != 1 || answers.Count != 1 => "判断题必须且只能选择一个答案。",
            "SingleChoice" or "MultipleChoice" or "TrueFalse" => null,
            _ => "不支持的题型。"
        };
        if (error is not null) return new GradeDecision(false, false, error);
        var isCorrect = chosen.SetEquals(answers);
        return new GradeDecision(true, isCorrect, null);
    }

    private static string Normalize(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized is "正确" or "TRUE" or "1") return "A";
        if (normalized is "错误" or "FALSE" or "0") return "B";
        return normalized;
    }
}

public sealed record GradeDecision(bool IsValid, bool IsCorrect, string? Error);
