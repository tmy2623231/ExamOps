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
public sealed class ExamAssignmentsController(
    AssignmentService assignments,
    IUnitOfWork uow,
    AuditRepository audit,
    ICurrentUser currentUser) : ExamApiControllerBase(uow, audit, currentUser)
{
    [HttpPost("exam-tasks/{taskId:long}/assignments/preview")]
    public async Task<IActionResult> Preview(long taskId, [FromBody] AssignmentTargetInput target)
    {
        if (await EnsurePermissionAsync(Permissions.ExamAssignmentCreate) is { } denied) return denied;
        return ToActionResult(await assignments.PreviewAsync(taskId, target));
    }

    [HttpPost("exam-tasks/{taskId:long}/assignments")]
    public async Task<IActionResult> Assign(long taskId, [FromBody] AssignExamInput input)
    {
        var api = await MetadataAsync(Permissions.ExamAssignmentCreate, true,
            "/api/exam-tasks/{taskId}/assignments", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync<AssignmentMutationResult>(api.Value) is { } replay) return replay;
        return ToActionResult(await assignments.AssignAsync(taskId, input, api.Value));
    }

    [HttpGet("exam-tasks/{taskId:long}/assignments")]
    public async Task<IActionResult> List(long taskId, [FromQuery] string? status,
        [FromQuery] string? keyword, [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        if (await EnsurePermissionAsync(Permissions.ExamAssignmentRead) is { } denied) return denied;
        var result = await ExecuteReadNonNullAsync(() => assignments.GetPagedAsync(
            new AssignmentQuery(taskId, status, keyword, pageIndex, pageSize)));
        return result is null ? NotFoundResource("ASSIGNMENT_NOT_FOUND") : Paged(result);
    }

    [HttpGet("exam-assignments/{assignmentId:long}")]
    public async Task<IActionResult> Detail(long assignmentId)
    {
        if (await EnsurePermissionAsync(Permissions.ExamAssignmentRead) is { } denied) return denied;
        var result = await ExecuteReadAsync(() => assignments.GetRowAsync(assignmentId));
        return result is null ? NotFoundResource("ASSIGNMENT_NOT_FOUND") : Ok(ApiEnvelope<ExamAssignmentRow>.Ok(result));
    }

    [HttpPost("exam-assignments/{assignmentId:long}/revoke")]
    public async Task<IActionResult> Revoke(long assignmentId, [FromBody] RevokeAssignmentInput input)
    {
        var api = await MetadataAsync(Permissions.ExamAssignmentRevoke, true,
            "/api/exam-assignments/{assignmentId}/revoke", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await assignments.RevokeAsync(assignmentId, input, api.Value));
    }

    [HttpPost("exam-assignments/{assignmentId:long}/grant-retake")]
    public async Task<IActionResult> GrantRetake(long assignmentId, [FromBody] GrantRetakeInput input)
    {
        var api = await MetadataAsync(Permissions.ExamAssignmentGrantRetake, true,
            "/api/exam-assignments/{assignmentId}/grant-retake", input);
        if (api.Error is not null) return api.Error;
        if (await ReplaySuccessAsync(api.Value) is { } replay) return replay;
        return ToActionResult(await assignments.GrantRetakeAsync(assignmentId, input, api.Value));
    }

    [HttpGet("assignment-batches/{batchId:long}")]
    public async Task<IActionResult> BatchDetail(long batchId,
        [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        if (await EnsurePermissionAsync(Permissions.ExamAssignmentRead) is { } denied) return denied;
        var result = await ExecuteReadAsync(() => assignments.GetBatchDetailAsync(batchId, pageIndex, pageSize));
        return result is null ? NotFoundResource("ASSIGNMENT_NOT_FOUND") : Ok(ApiEnvelope<AssignmentBatchDetail>.Ok(result));
    }

    [HttpGet("assignment-batches/{batchId:long}/rows")]
    public async Task<IActionResult> BatchRows(long batchId,
        [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        if (await EnsurePermissionAsync(Permissions.ExamAssignmentRead) is { } denied) return denied;
        var result = await ExecuteReadAsync(() => assignments.GetBatchDetailAsync(batchId, pageIndex, pageSize));
        if (result is null) return NotFoundResource("ASSIGNMENT_NOT_FOUND");
        return Paged(result.Rows);
    }

    [HttpPost("assignment-batches/{batchId:long}/retry")]
    public async Task<IActionResult> RetryBatch(long batchId, [FromBody] RetryBatchApiInput input)
    {
        var api = await MetadataAsync(Permissions.ExamAssignmentCreate, true,
            "/api/assignment-batches/{batchId}/retry", input);
        if (api.Error is not null) return api.Error;
        var (replay, replayError) = await FindReplayAsync<Dictionary<string, long>>(api.Value);
        if (replayError is not null) return replayError;
        if (replay is not null && replay.TryGetValue("batchId", out var replayBatchId))
        {
            var detail = await ExecuteReadAsync(() => assignments.GetBatchDetailAsync(replayBatchId, 1, 1));
            return detail is null
                ? NotFoundResource("ASSIGNMENT_NOT_FOUND")
                : Ok(ApiEnvelope<AssignmentMutationResult>.Ok(new(replayBatchId, detail.TaskId), true));
        }
        return ToActionResult(await assignments.RetryBatchAsync(batchId, input.RowVersion, api.Value));
    }
}

public sealed record RetryBatchApiInput(long RowVersion);
