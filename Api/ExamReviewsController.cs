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
public sealed class ExamReviewsController(
    ExamReviewService reviews,
    IUnitOfWork uow,
    AuditRepository audit,
    ICurrentUser currentUser) : ExamApiControllerBase(uow, audit, currentUser)
{
    [HttpGet("reviews/queue")]
    public async Task<IActionResult> Queue([FromQuery] ExamReviewQuery query)
    {
        if (await EnsurePermissionAsync(Permissions.ExamAttemptRead) is { } denied) return denied;
        if (query.ReviewType is not (null or ReviewTypes.FormatAnomaly or ReviewTypes.Dispute or ReviewTypes.VoidRequest))
            return BadRequest(ApiEnvelope<object>.Fail("复核类型无效", "VALIDATION_ERROR"));
        var result = await ExecuteReadNonNullAsync(() => reviews.GetQueueAsync(query));
        return Paged(result);
    }

    [HttpPost("attempts/{attemptId:long}/review-events")]
    public async Task<IActionResult> AddReviewEvent(long attemptId, [FromBody] ReviewEventInput input)
    {
        var api = await MetadataAsync(Permissions.ExamAttemptGrade, true,
            "/api/attempts/{attemptId}/review-events", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync<ReviewEventResult>(api.Value) is { } replay) return replay;
        return ToActionResult(await reviews.AddReviewEventAsync(attemptId, input, api.Value));
    }

    [HttpPost("attempts/{attemptId:long}/adjust-score")]
    public async Task<IActionResult> AdjustScore(long attemptId, [FromBody] AdjustScoreInput input)
    {
        var api = await MetadataAsync(Permissions.ExamAttemptAdjustScore, true,
            "/api/attempts/{attemptId}/adjust-score", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync<AdjustScoreResult>(api.Value, value => value with { Replayed = true }) is { } replay)
            return replay;
        return ToActionResult(await reviews.AdjustScoreAsync(attemptId, input, api.Value));
    }

    [HttpGet("attempts/{attemptId:long}/score-adjustments")]
    public async Task<IActionResult> Adjustments(long attemptId, [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        if (await EnsurePermissionAsync(Permissions.ExamAttemptAdjustScore) is { } denied) return denied;
        var result = await ExecuteReadNonNullAsync(() => reviews.GetAdjustmentsAsync(attemptId, pageIndex, pageSize)!);
        return result is null ? NotFoundResource("ATTEMPT_NOT_FOUND") : Paged(result);
    }
}
