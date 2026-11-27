using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamOps.Api;

[ApiController]
[Route("api")]
[Authorize]
public sealed class ExamResultsController(
    ResultReleaseService results,
    IUnitOfWork uow,
    AuditRepository audit,
    ICurrentUser currentUser) : ExamApiControllerBase(uow, audit, currentUser)
{
    [HttpPost("exam-tasks/{taskId:long}/result/publish")]
    public async Task<IActionResult> Publish(long taskId, [FromBody] PublishResultInput input)
    {
        var api = await MetadataAsync(Permissions.ExamResultPublish, true,
            "/api/exam-tasks/{taskId}/result/publish", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync<PublishResultOutput>(api.Value) is { } replay) return replay;
        return ToActionResult(await results.PublishAsync(taskId, input, api.Value));
    }

    [HttpPost("exam-tasks/{taskId:long}/result/withdraw")]
    public async Task<IActionResult> Withdraw(long taskId, [FromBody] WithdrawResultInput input)
    {
        var api = await MetadataAsync(Permissions.ExamResultPublish, true,
            "/api/exam-tasks/{taskId}/result/withdraw", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync<WithdrawResultOutput>(api.Value) is { } replay) return replay;
        return ToActionResult(await results.WithdrawAsync(taskId, input, api.Value));
    }

    [HttpGet("exam-tasks/{taskId:long}/result/releases")]
    public async Task<IActionResult> Releases(long taskId)
    {
        if (await EnsurePermissionAsync(Permissions.ExamResultPublish) is { } denied) return denied;
        try { return Ok(ApiEnvelope<object>.Ok(await results.GetHistoryAsync(taskId))); }
        catch (InvalidOperationException ex) when (ex.Message is "NOT_FOUND" or "PERMISSION_DENIED")
        { return NotFoundResource("EXAM_TASK_NOT_FOUND"); }
    }
}
