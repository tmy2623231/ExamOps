using ExamOps.Model.Common;
using ExamOps.Model.Dtos;

namespace ExamOps.BLL.Validators;

public static class PaperValidator
{
    public static Result Validate(PaperVersionInput input)
    {
        if (input.Title.Trim().Length is 0 or > 200) return Result.Fail("试卷名称需为 1–200 个字符", "INVALID_TITLE");
        if (input.DurationMinutes is < 1 or > 600) return Result.Fail("考试时长需为 1–600 分钟", "INVALID_DURATION");
        if (input.Sections.Count > 100) return Result.Fail("试卷最多支持 100 个小节", "TOO_MANY_SECTIONS");
        var sorts = new HashSet<int>();
        decimal totalScore = 0;
        foreach (var section in input.Sections)
        {
            if (section.Title.Trim().Length is 0 or > 128) return Result.Fail("小节名称需为 1–128 个字符", "INVALID_SECTION_TITLE");
            if (section.SortOrder <= 0 || !sorts.Add(section.SortOrder)) return Result.Fail("小节顺序必须为正整数且不能重复", "INVALID_SECTION_ORDER");
            if (section.SelectionMode is not ("Fixed" or "Random")) return Result.Fail("小节组题方式无效", "INVALID_SELECTION_MODE");
            if (section.ScorePerQuestion is <= 0 or > 1000) return Result.Fail("每题分值需大于 0 且不超过 1000", "INVALID_SCORE");
            if (section.SelectionMode == "Fixed")
            {
                if (section.PickCount.HasValue || section.BankId.HasValue || section.CategoryId.HasValue
                    || section.QuestionType is not null || section.Difficulty is not null)
                    return Result.Fail("固定小节不能设置随机蓝图条件", "FIXED_BLUEPRINT_FORBIDDEN");
            }
            else
            {
                if (section.PickCount is < 1 or > 200) return Result.Fail("随机抽题数需为 1–200", "INVALID_PICK_COUNT");
                if (section.QuestionType is not null && section.QuestionType is not ("SingleChoice" or "MultipleChoice" or "TrueFalse"))
                    return Result.Fail("题型条件无效", "INVALID_QUESTION_TYPE");
                if (section.Difficulty is not null && section.Difficulty is not ("Easy" or "Medium" or "Hard"))
                    return Result.Fail("难度条件无效", "INVALID_DIFFICULTY");
            }
            var questionCount = section.SelectionMode == "Random" ? section.PickCount ?? 0 : section.QuestionVersionIds.Distinct().Count();
            totalScore += questionCount * section.ScorePerQuestion;
            if (totalScore > 9999.99m) return Result.Fail("试卷总分不能超过 9999.99 分", "TOTAL_SCORE_TOO_HIGH");
        }
        return Result.Ok();
    }

    public static Result? ValidateTotal(decimal totalScore)
        => totalScore <= 9999.99m ? null : Result.Fail("试卷总分不能超过 9999.99 分", "TOTAL_SCORE_TOO_HIGH");

    public static Result ValidateCreate(CreatePaperInput input)
        => Validate(new PaperVersionInput(input.Title, input.DurationMinutes, []));
}
