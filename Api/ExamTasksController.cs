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
public sealed class ExamTasksController(
    ExamTaskService tasks,
    IUnitOfWork uow,
    AuditRepository audit,
    ICurrentUser currentUser) : ExamApiControllerBase(uow, audit, currentUser)
{
    [HttpGet("exam-tasks")]
    public async Task<IActionResult> List([FromQuery] string? keyword, [FromQuery] long? departmentId,
        [FromQuery] string? status, [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        if (await EnsurePermissionAsync(Permissions.ExamTaskRead) is { } denied) return denied;
        var result = await ExecuteReadNonNullAsync(() => tasks.GetPagedAsync(
            new ExamTaskQuery(keyword, departmentId, status, pageIndex, pageSize)));
        return result is null
            ? NotFoundResource("EXAM_TASK_NOT_FOUND")
            : Paged(result);
    }

    [HttpGet("exam-tasks/{taskId:long}")]
    public async Task<IActionResult> Detail(long taskId)
    {
        if (await EnsurePermissionAsync(Permissions.ExamTaskRead) is { } denied) return denied;
        var result = await ExecuteReadAsync(() => tasks.GetDetailAsync(taskId));
        return result is null ? NotFoundResource("EXAM_TASK_NOT_FOUND") : Ok(ApiEnvelope<ExamTaskDetail>.Ok(result));
    }

    [HttpGet("exam-tasks/{taskId:long}/staff")]
    public async Task<IActionResult> Staff(long taskId)
    {
        if (await EnsurePermissionAsync(Permissions.ExamTaskRead) is { } denied) return denied;
        if (await ExecuteReadAsync(() => tasks.GetDetailAsync(taskId)) is null)
            return NotFoundResource("EXAM_TASK_NOT_FOUND");
        var result = await ExecuteReadNonNullAsync(() => tasks.GetStaffAsync(taskId));
        return result is null
            ? NotFoundResource("EXAM_TASK_NOT_FOUND")
            : Ok(ApiEnvelope<IReadOnlyList<ExamTaskStaffRow>>.Ok(result));
    }

    [HttpPost("exam-tasks")]
    public async Task<IActionResult> Create([FromBody] CreateExamTaskInput input)
    {
        var api = await MetadataAsync(Permissions.ExamTaskCreate, true, "/api/exam-tasks", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync<ExamTaskMutationResult>(api.Value) is { } replay) return replay;
        return ToActionResult(await tasks.CreateAsync(input, api.Value));
    }

    [HttpPut("exam-tasks/{taskId:long}")]
    public async Task<IActionResult> Update(long taskId, [FromBody] UpdateExamTaskInput input)
    {
        var api = await MetadataAsync(Permissions.ExamTaskUpdate, true, "/api/exam-tasks/{taskId}", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await tasks.UpdateDraftAsync(taskId, input, api.Value));
    }

    [HttpPost("exam-tasks/{taskId:long}/publish")]
    public async Task<IActionResult> Publish(long taskId, [FromBody] ExamTaskActionInput input)
    {
        var api = await MetadataAsync(Permissions.ExamTaskPublish, true, "/api/exam-tasks/{taskId}/publish", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await tasks.PublishAsync(taskId, input, api.Value));
    }

    [HttpPost("exam-tasks/{taskId:long}/close")]
    public async Task<IActionResult> Close(long taskId, [FromBody] ExamTaskActionInput input)
    {
        var api = await MetadataAsync(Permissions.ExamTaskClose, true, "/api/exam-tasks/{taskId}/close", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await tasks.CloseAsync(taskId, input, api.Value));
    }

    [HttpPost("exam-tasks/{taskId:long}/reopen")]
    public async Task<IActionResult> Reopen(long taskId, [FromBody] ExamTaskActionInput input)
    {
        var api = await MetadataAsync(Permissions.ExamTaskPublish, true, "/api/exam-tasks/{taskId}/reopen", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await tasks.ReopenAsync(taskId, input, api.Value));
    }

    [HttpPost("exam-tasks/{taskId:long}/revoke")]
    public async Task<IActionResult> Revoke(long taskId, [FromBody] RevokeExamTaskInput input)
    {
        var api = await MetadataAsync(Permissions.ExamTaskRevoke, true, "/api/exam-tasks/{taskId}/revoke", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await tasks.RevokeAsync(taskId, input, api.Value));
    }

    [HttpPost("exam-tasks/{taskId:long}/archive")]
    public async Task<IActionResult> Archive(long taskId, [FromBody] ExamTaskActionInput input)
    {
        var api = await MetadataAsync(Permissions.ExamTaskUpdate, true, "/api/exam-tasks/{taskId}/archive", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await tasks.ArchiveAsync(taskId, input, api.Value));
    }

    [HttpPost("exam-tasks/{taskId:long}/staff")]
    public async Task<IActionResult> AddStaff(long taskId, [FromBody] ExamTaskStaffInput input)
    {
        var api = await MetadataAsync(Permissions.ExamTaskUpdate, true, "/api/exam-tasks/{taskId}/staff", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await tasks.AddStaffAsync(taskId, input, api.Value));
    }

    [HttpDelete("exam-tasks/{taskId:long}/staff/{staffId:long}")]
    public async Task<IActionResult> RemoveStaff(long taskId, long staffId)
    {
        var api = await MetadataAsync(Permissions.ExamTaskUpdate, true,
            "/api/exam-tasks/{taskId}/staff/{staffId}", new { staffId });
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await tasks.RemoveStaffAsync(taskId, staffId, api.Value));
    }
}
