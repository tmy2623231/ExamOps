using System.Text.Json;

namespace ExamOps.Model.Common;

public sealed record QuestionAnswerValues(string Value, IReadOnlyList<string> Values, bool TrueFalseAnswer)
{
    public static QuestionAnswerValues Empty => new("", [], false);
}

public static class QuestionAnswerJson
{
    public static QuestionAnswerValues Read(string json, string type)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any())
            return QuestionAnswerValues.Empty;
        return type switch
        {
            "SingleChoice" => new QuestionAnswerValues(root.GetProperty("value").GetString() ?? "", [], false),
            "MultipleChoice" => new QuestionAnswerValues("", root.GetProperty("values").EnumerateArray()
                .Select(x => x.GetString() ?? "").OrderBy(x => x, StringComparer.Ordinal).ToArray(), false),
            "TrueFalse" => new QuestionAnswerValues("", [], root.GetProperty("value").GetBoolean()),
            _ => QuestionAnswerValues.Empty
        };
    }

    public static string Serialize(string type, string answerValue, IEnumerable<string> answerValues, bool trueFalseAnswer)
        => type switch
        {
            "SingleChoice" => JsonSerializer.Serialize(new { value = answerValue }),
            "MultipleChoice" => JsonSerializer.Serialize(new { values = answerValues.OrderBy(x => x, StringComparer.Ordinal).Distinct(StringComparer.Ordinal) }),
            "TrueFalse" => JsonSerializer.Serialize(new { value = trueFalseAnswer }),
            _ => throw new ArgumentException("不支持的题型。", nameof(type))
        };
}
