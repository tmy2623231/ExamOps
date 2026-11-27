using System.Globalization;

namespace ExamOps.Infra.Import;

public static class XlsxImportRules
{
    public static readonly string[] HeaderNames =
    [
        "题目编码", "题型", "题干", "选项A", "选项B", "选项C", "选项D", "选项E", "选项F",
        "正确答案", "分值", "难度", "标签", "解析"
    ];

    internal static QuestionImportParsedRow ValidateRow(int rowNo, IReadOnlyList<string> cells)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var externalCode = Normalize(cells[0]);
        var type = NormalizeType(cells[1], errors);
        var stem = Normalize(cells[2]);
        if (stem.Length == 0) errors.Add("题干不能为空。");

        var difficulty = NormalizeDifficulty(cells[11], errors, warnings);
        var defaultScore = ParseScore(cells[10], errors);
        var analysis = Normalize(cells[13]);
        var options = ParseOptions(cells, type, errors, warnings);
        var (answerValue, answerValues, trueFalseAnswer) = ParseAnswer(cells[9], type, options, errors);
        var tagNames = ParseTags(cells[12], errors);

        if (externalCode.Length > 128) errors.Add("题目编码不能超过128个字符。");

        return new QuestionImportParsedRow(
            rowNo,
            externalCode.Length == 0 ? null : externalCode,
            type,
            stem,
            difficulty,
            defaultScore,
            analysis.Length == 0 ? null : analysis,
            options,
            answerValue,
            answerValues,
            trueFalseAnswer,
            tagNames,
            errors.ToArray(),
            warnings.ToArray());
    }

    private static string NormalizeType(string value, List<string> errors)
    {
        return value switch
        {
            "单选" => "SingleChoice",
            "多选" => "MultipleChoice",
            "判断" => "TrueFalse",
            "" => AddTypeError(errors, "题型不能为空。"),
            _ => AddTypeError(errors, "题型必须为单选、多选或判断。")
        };
    }

    private static string AddTypeError(List<string> errors, string message)
    {
        errors.Add(message);
        return "";
    }

    private static string NormalizeDifficulty(string value, List<string> errors, List<string> warnings)
    {
        if (value.Length == 0)
        {
            warnings.Add("难度为空，已默认中等。");
            return "Medium";
        }

        return value switch
        {
            "易" => "Easy",
            "中" => "Medium",
            "难" => "Hard",
            _ => AddDifficultyError(errors)
        };
    }

    private static string AddDifficultyError(List<string> errors)
    {
        errors.Add("难度必须为易、中或难。");
        return "";
    }

    private static decimal ParseScore(string value, List<string> errors)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var score)
            || score < 0.01m
            || score > 9999.99m)
        {
            errors.Add("分值必须为0.01至9999.99之间的数字。");
            return 0m;
        }

        if (decimal.Round(score, 2) != score)
        {
            errors.Add("分值最多保留两位小数。");
        }

        return score;
    }

    private static IReadOnlyList<ParsedQuestionOption> ParseOptions(
        IReadOnlyList<string> cells,
        string type,
        List<string> errors,
        List<string> warnings)
    {
        var values = cells.Skip(3).Take(6).Select(Normalize).ToArray();
        if (type == "TrueFalse")
        {
            if (values.Any(value => value.Length > 0))
            {
                warnings.Add("判断题选项已忽略，由系统生成正确/错误。");
            }

            return
            [
                new ParsedQuestionOption("A", "正确"),
                new ParsedQuestionOption("B", "错误")
            ];
        }

        var options = new List<ParsedQuestionOption>();
        if (type is "SingleChoice" or "MultipleChoice")
        {
            var lastNonEmpty = Array.FindLastIndex(values, value => value.Length > 0);
            for (var i = 0; i <= lastNonEmpty; i++)
            {
                var label = ((char)('A' + i)).ToString();
                if (values[i].Length == 0)
                {
                    errors.Add($"选项{label}不能为空。");
                    continue;
                }

                options.Add(new ParsedQuestionOption(label, values[i]));
            }

            if (options.Count < 2) errors.Add("选择题至少需要2个选项。");
        }
        else
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i].Length > 0)
                {
                    options.Add(new ParsedQuestionOption(((char)('A' + i)).ToString(), values[i]));
                }
            }
        }

        return options.ToArray();
    }

    private static (string AnswerValue, IReadOnlyList<string> AnswerValues, bool TrueFalseAnswer) ParseAnswer(
        string rawValue,
        string type,
        IReadOnlyList<ParsedQuestionOption> options,
        List<string> errors)
    {
        var value = Normalize(rawValue);
        if (type == "TrueFalse")
        {
            if (value.Length == 0)
            {
                errors.Add("正确答案不能为空。");
                return (value, [], false);
            }

            return value.ToLowerInvariant() switch
            {
                "正确" or "true" => ("正确", [], true),
                "错误" or "false" => ("错误", [], false),
                _ => AddTrueFalseError(errors, value)
            };
        }

        if (type == "SingleChoice")
        {
            var answer = value.ToUpperInvariant();
            if (answer.Length != 1 || answer[0] < 'A' || answer[0] > 'F')
            {
                errors.Add(value.Length == 0 ? "正确答案不能为空。" : "单选题正确答案必须为A至F中的一个选项。");
                return (value, [], false);
            }

            ValidateAnswerOption(answer, options, errors);
            return (answer, [answer], false);
        }

        if (type == "MultipleChoice")
        {
            if (value.Length == 0)
            {
                errors.Add("正确答案不能为空。");
                return (value, [], false);
            }

            var tokens = value
                .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(part => part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Select(token => token.ToUpperInvariant())
                .ToArray();
            var valid = tokens
                .Where(token => token.Length == 1 && token[0] is >= 'A' and <= 'F')
                .Distinct(StringComparer.Ordinal)
                .OrderBy(token => token, StringComparer.Ordinal)
                .ToArray();
            var hasInvalidTokens = tokens.Any(token => token.Length != 1 || token[0] is < 'A' or > 'F');
            if (hasInvalidTokens)
            {
                errors.Add("多选题正确答案只能包含A至F的选项字母，并使用逗号、空格或分号分隔。");
            }

            foreach (var answer in valid)
            {
                ValidateAnswerOption(answer, options, errors);
            }

            return (hasInvalidTokens ? value : string.Join(',', valid), valid, false);
        }

        if (value.Length == 0) errors.Add("正确答案不能为空。");
        return (value, [], false);
    }

    private static (string, IReadOnlyList<string>, bool) AddTrueFalseError(List<string> errors, string value)
    {
        errors.Add("判断题正确答案必须为正确、错误、true或false。");
        return (value, [], false);
    }

    private static void ValidateAnswerOption(
        string answer,
        IReadOnlyList<ParsedQuestionOption> options,
        List<string> errors)
    {
        if (options.All(option => option.Label != answer))
        {
            errors.Add($"正确答案“{answer}”没有对应选项。");
        }
    }

    private static IReadOnlyList<string> ParseTags(string value, List<string> errors)
    {
        var tags = value
            .Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var tag in tags)
        {
            if (tag.Length > 128) errors.Add("单个标签不能超过128个字符。");
        }

        return tags;
    }

    private static string Normalize(string value) => value.Trim();
}
