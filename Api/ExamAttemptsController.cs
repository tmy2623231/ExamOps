using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamOps.Api;

[ApiController]
[Route("api")]
[Authorize]
public sealed class ExamAttemptsController(
    ExamAttemptService attempts,
    IUnitOfWork uow,
    AuditRepository audit,
    ICurrentUser currentUser) : ExamApiControllerBase(uow, audit, currentUser)
{
    [HttpGet("my/exams")]
    public async Task<IActionResult> MyExams([FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        if (await EnsureAuthenticatedAsync() is { } denied) return denied;
        var result = await ExecuteReadNonNullAsync(() => attempts.GetMyExamsAsync(pageIndex, pageSize));
        return result is null
            ? StatusCode(StatusCodes.Status401Unauthorized, ApiEnvelope<object>.Fail("请先登录", "UNAUTHENTICATED"))
            : Paged(result);
    }

    [HttpPost("my/exams/{assignmentId:long}/start")]
    public async Task<IActionResult> Start(long assignmentId, [FromBody] StartExamInput input)
    {
        var api = await MetadataAsync(null, true, "/api/my/exams/{assignmentId}/start", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync<StartAttemptResult>(api.Value) is { } replay) return replay;
        return ToActionResult(await attempts.StartAsync(assignmentId, input, api.Value));
    }

    [HttpGet("attempts/{attemptId:long}")]
    public async Task<IActionResult> Detail(long attemptId)
    {
        if (await EnsureAuthenticatedAsync() is { } denied) return denied;
        var result = await ExecuteReadAsync(() => attempts.GetDetailAsync(attemptId));
        return result is null ? NotFoundResource("ATTEMPT_NOT_FOUND") : Ok(ApiEnvelope<AttemptDetail>.Ok(result));
    }

    [HttpPut("attempts/{attemptId:long}/answers/{questionVersionId:long}")]
    public async Task<IActionResult> SaveAnswer(long attemptId, long questionVersionId, [FromBody] SaveAnswerInput input)
    {
        var payload = new SaveAnswersInput
        {
            RowVersion = input.RowVersion,
            Answers = [new SaveAnswerItemInput(questionVersionId, input.AnswerJson)]
        };
        var api = await MetadataAsync(null, false, "/api/attempts/{attemptId}/answers/{questionVersionId}", payload);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await attempts.SaveAnswersAsync(attemptId, payload, api.Value));
    }

    [HttpPut("attempts/{attemptId:long}/answers")]
    public async Task<IActionResult> SaveAnswers(long attemptId, [FromBody] SaveAnswersInput input)
    {
        var api = await MetadataAsync(null, false, "/api/attempts/{attemptId}/answers", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await attempts.SaveAnswersAsync(attemptId, input, api.Value));
    }

    [HttpPost("attempts/{attemptId:long}/submit")]
    public async Task<IActionResult> Submit(long attemptId, [FromBody] SubmitAttemptInput input)
    {
        var api = await MetadataAsync(null, true, "/api/attempts/{attemptId}/submit", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync<AttemptSubmitResult>(api.Value, value => value with { Replayed = true }) is { } replay)
            return replay;
        return ToActionResult(await attempts.SubmitAsync(attemptId, input, api.Value));
    }

    [HttpGet("attempts/{attemptId:long}/result")]
    public async Task<IActionResult> Result(long attemptId)
    {
        if (await EnsureAuthenticatedAsync() is { } denied) return denied;
        var result = await ExecuteReadAsync(() => attempts.GetResultAsync(attemptId));
        return result is null ? NotFoundResource("ATTEMPT_NOT_FOUND") : Ok(ApiEnvelope<AttemptResultDto>.Ok(result));
    }

    [HttpPost("attempts/{attemptId:long}/void")]
    public async Task<IActionResult> Void(long attemptId, [FromBody] VoidAttemptInput input)
    {
        var api = await MetadataAsync(Permissions.ExamAttemptVoid, true, "/api/attempts/{attemptId}/void", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await attempts.VoidAsync(attemptId, input, api.Value));
    }
}
