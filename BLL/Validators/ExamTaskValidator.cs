using ExamOps.BLL.Validators;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using ExamOps.Model.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExamOps.BLL.Validators;

public static class ExamTaskValidator
{
    public static Result Validate(CreateExamTaskInput input)
    {
        if (input.Code.Trim().Length is 0 or > 64) return Result.Fail("任务编码需为 1–64 个字符", "INVALID_CODE");
        if (input.Title.Trim().Length is 0 or > 200) return Result.Fail("任务名称需为 1–200 个字符", "INVALID_TITLE");
        if (input.Description?.Length > 4000) return Result.Fail("任务说明不能超过 4000 个字符", "INVALID_DESCRIPTION");
        if (input.AvailableTo <= input.AvailableFrom) return Result.Fail("结束时间必须晚于开始时间", "EXAM_WINDOW_INVALID");
        if (input.DurationMinutes is < 1 or > 1440) return Result.Fail("考试时长需为 1–1440 分钟", "INVALID_DURATION");
        if (input.AttemptLimit < 1) return Result.Fail("允许次数至少为 1", "INVALID_ATTEMPT_LIMIT");
        if (input.ScorePolicy is not ("Latest" or "Highest" or "First" or "Average")) return Result.Fail("成绩策略无效", "VALIDATION_ERROR");
        if (input.RetakeApproval is not ("None" or "Manager")) return Result.Fail("重考审批方式无效", "VALIDATION_ERROR");
        if (input.RetakeCooldownMinutes < 0) return Result.Fail("重考冷却时间不能为负数", "VALIDATION_ERROR");
        if (input.PassScore is < 0 or > 9999.99m) return Result.Fail("及格分必须在 0–9999.99 之间", "VALIDATION_ERROR");
        if (input.ResultReleasePolicy is not ("Immediate" or "AfterClose" or "Manual")) return Result.Fail("成绩发布策略无效", "VALIDATION_ERROR");
        if (input.LateScorePolicy is not ("Score" or "Zero")) return Result.Fail("迟到成绩策略无效", "VALIDATION_ERROR");
        return Result.Ok();
    }

    public static Result Validate(UpdateExamTaskInput input)
    {
        if (input.Title.Trim().Length is 0 or > 200) return Result.Fail("任务名称需为 1–200 个字符", "INVALID_TITLE");
        if (input.Description?.Length > 4000) return Result.Fail("任务说明不能超过 4000 个字符", "INVALID_DESCRIPTION");
        if (input.AvailableTo <= input.AvailableFrom) return Result.Fail("结束时间必须晚于开始时间", "EXAM_WINDOW_INVALID");
        if (input.DurationMinutes is < 1 or > 1440) return Result.Fail("考试时长需为 1–1440 分钟", "INVALID_DURATION");
        if (input.AttemptLimit < 1) return Result.Fail("允许次数至少为 1", "INVALID_ATTEMPT_LIMIT");
        if (input.ScorePolicy is not ("Latest" or "Highest" or "First" or "Average")) return Result.Fail("成绩策略无效", "VALIDATION_ERROR");
        if (input.RetakeApproval is not ("None" or "Manager")) return Result.Fail("重考审批方式无效", "VALIDATION_ERROR");
        if (input.RetakeCooldownMinutes < 0) return Result.Fail("重考冷却时间不能为负数", "VALIDATION_ERROR");
        if (input.PassScore is < 0 or > 9999.99m) return Result.Fail("及格分必须在 0–9999.99 之间", "VALIDATION_ERROR");
        if (input.ResultReleasePolicy is not ("Immediate" or "AfterClose" or "Manual")) return Result.Fail("成绩发布策略无效", "VALIDATION_ERROR");
        if (input.LateScorePolicy is not ("Score" or "Zero")) return Result.Fail("迟到成绩策略无效", "VALIDATION_ERROR");
        return Result.Ok();
    }

    public static Result ValidateReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10
            ? Result.Fail("撤回原因至少需要 10 个字符", "REASON_REQUIRED") : Result.Ok();
}
