using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed class PapersController(
    PaperService papers,
    IUnitOfWork uow,
    AuditRepository audit,
    ICurrentUser currentUser) : ControllerBase
{
    [HttpGet("papers")]
    public async Task<IActionResult> List([FromQuery] string? keyword, [FromQuery] long? departmentId,
        [FromQuery] string? status, [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        if (await EnsureReadAccessAsync() is { } denied) return denied;
        var result = await ExecuteReadAsync(() => papers.GetPagedAsync(new(keyword, departmentId, status, pageIndex, pageSize)));
        return result is null ? NotFound(ApiEnvelope<object>.Fail("无权查看试卷", "PAPER_NOT_FOUND"))
            : Ok(ApiEnvelope<object>.Ok(new { items = result.Items, totalCount = result.TotalCount, pageIndex = result.PageIndex, pageSize = result.PageSize }));
    }

    [HttpGet("papers/{paperId:long}")]
    public async Task<IActionResult> Detail(long paperId, [FromQuery] long? versionId)
    {
        if (await EnsureReadAccessAsync() is { } denied) return denied;
        PaperDetail? result;
        try { result = await papers.GetDetailAsync(paperId, versionId); }
        catch (UnauthorizedAccessException) { result = null; }
        return result is null ? NotFound(ApiEnvelope<object>.Fail("试卷不存在", "PAPER_NOT_FOUND")) : Ok(ApiEnvelope<PaperDetail>.Ok(result));
    }

    [HttpPost("papers")]
    public async Task<IActionResult> Create([FromBody] CreatePaperInput input)
    {
        var api = await MetadataAsync(Permissions.PaperCreate, false, "/api/papers", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync(api.Value!) is { } replay) return replay;
        return ToActionResult(await papers.CreateAsync(input, api.Value));
    }

    [HttpPut("papers/{paperId:long}/versions/{versionId:long}")]
    public async Task<IActionResult> Update(long paperId, long versionId, [FromBody] PaperUpdateApiInput input)
    {
        var api = await MetadataAsync(Permissions.PaperUpdate, false, "/api/papers/{paperId}/versions/{versionId}", input, true);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync(api.Value, mutationResult: false) is { } replay) return replay;
        return ToActionResult(await papers.UpdateDraftAsync(paperId, versionId, input.RowVersion, input.VersionRowVersion,
            new(input.Title, input.DurationMinutes, input.Sections), api.Value));
    }

    [HttpPost("papers/{paperId:long}/versions")]
    public async Task<IActionResult> Copy(long paperId, [FromBody] PaperCopyApiInput input)
    {
        var api = await MetadataAsync(Permissions.PaperUpdate, true, "/api/papers/{paperId}/versions", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync(api.Value!) is { } replay) return replay;
        return ToActionResult(await papers.CopyDraftAsync(paperId, input.RowVersion, api.Value));
    }

    [HttpPut("papers/{paperId:long}/versions/{versionId:long}/sections")]
    public async Task<IActionResult> ReplaceSections(long paperId, long versionId, [FromBody] PaperSectionReplaceApiInput input)
    {
        var api = await MetadataAsync(Permissions.PaperUpdate, false, "/api/papers/{paperId}/versions/{versionId}/sections", input, true);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync(api.Value, mutationResult: false) is { } replay) return replay;
        return ToActionResult(await papers.ReplaceSectionsAsync(paperId, versionId, input.RowVersion, input.VersionRowVersion, input.Sections, api.Value));
    }

    [HttpPost("papers/{paperId:long}/versions/{versionId:long}/questions")]
    public async Task<IActionResult> AddQuestion(long paperId, long versionId, [FromBody] PaperAddQuestionApiInput input)
    {
        var api = await MetadataAsync(Permissions.PaperUpdate, false, "/api/papers/{paperId}/versions/{versionId}/questions", input, true);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync(api.Value!, mutationResult: false) is { } replay) return replay;
        return ToActionResult(await papers.AddQuestionAsync(paperId, versionId, input.SectionId, input.QuestionVersionId,
            input.RowVersion, input.VersionRowVersion, api.Value));
    }

    [HttpDelete("papers/{paperId:long}/versions/{versionId:long}/questions/{paperQuestionId:long}")]
    public async Task<IActionResult> DeleteQuestion(long paperId, long versionId, long paperQuestionId, [FromBody] PaperDeleteQuestionApiInput input)
    {
        var api = await MetadataAsync(Permissions.PaperUpdate, false, "/api/papers/{paperId}/versions/{versionId}/questions/{paperQuestionId}", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync(api.Value!, mutationResult: false) is { } replay) return replay;
        return ToActionResult(await papers.DeleteQuestionAsync(paperId, versionId, paperQuestionId, input.RowVersion, input.VersionRowVersion, api.Value));
    }

    [HttpPost("papers/{paperId:long}/publish")]
    public async Task<IActionResult> Publish(long paperId, [FromBody] PaperPublishApiInput input)
    {
        var api = await MetadataAsync(Permissions.PaperPublish, true, "/api/papers/{paperId}/publish", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync(api.Value!, mutationResult: false) is { } replay) return replay;
        return ToActionResult(await papers.PublishAsync(paperId, new(input.VersionId, input.VersionRowVersion), input.RowVersion, api.Value));
    }

    [HttpPost("papers/{paperId:long}/archive")]
    public async Task<IActionResult> Archive(long paperId, [FromBody] PaperActionInput input)
    {
        var api = await MetadataAsync(Permissions.PaperArchive, true, "/api/papers/{paperId}/archive", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync(api.Value!, mutationResult: false) is { } replay) return replay;
        return ToActionResult(await papers.ArchiveAsync(paperId, input.RowVersion, api.Value));
    }

    private async Task<IActionResult?> ReplayAsync(ApiAuditMetadata? api, bool mutationResult = true)
    {
        if (api is null || string.IsNullOrWhiteSpace(api.IdempotencyKey) || currentUser.Id is not { } actorId) return null;
        var existing = await audit.FindApiAuditAsync(uow.Db, actorId, api);
        if (existing is null) return null;
        if (!string.Equals(existing.RequestHash, api.RequestHash, StringComparison.Ordinal))
            return Conflict(ApiEnvelope<object>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT"));
        if (!mutationResult) return Ok(ApiEnvelope<object>.Ok(new { success = true }, true));
        var value = JsonSerializer.Deserialize<PaperMutationResult>(existing.ResultRefJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Ok(ApiEnvelope<PaperMutationResult>.Ok(value!, true));
    }

    private async Task<(ApiAuditMetadata? Value, IActionResult? Error)> MetadataAsync(string permission, bool required,
        string routeTemplate, object payload, bool questionRead = false)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated) return (null, StatusCode(StatusCodes.Status401Unauthorized,
            ApiEnvelope<object>.Fail("请先登录", "UNAUTHENTICATED")));
        if (!currentUser.HasPermission(permission) || (questionRead && !currentUser.HasPermission(Permissions.QuestionRead)))
            return (null, StatusCode(StatusCodes.Status403Forbidden,
            ApiEnvelope<object>.Fail("权限不足", "PERMISSION_DENIED")));
        var key = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrEmpty(key))
            return required ? (null, BadRequest(ApiEnvelope<object>.Fail("缺少 Idempotency-Key", "IDEMPOTENCY_KEY_REQUIRED")))
                : (new ApiAuditMetadata(null, null, Request.Method, routeTemplate), null);
        if (key.Length > 128 || key.Any(char.IsWhiteSpace)) return (null, BadRequest(ApiEnvelope<object>.Fail("Idempotency-Key 格式无效", "IDEMPOTENCY_KEY_INVALID")));
        var canonical = JsonSerializer.Serialize(new { path = Request.Path.Value, query = Request.QueryString.Value, payload }, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return (new ApiAuditMetadata(key, hash, Request.Method, routeTemplate), null);
    }

    private async Task<IActionResult?> EnsureReadAccessAsync()
    {
        await currentUser.LoadAsync();
        return currentUser.IsAuthenticated && currentUser.HasPermission(Permissions.PaperRead)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, ApiEnvelope<object>.Fail("权限不足", "PERMISSION_DENIED"));
    }

    private static async Task<T?> ExecuteReadAsync<T>(Func<Task<T>> action) where T : class
    {
        try { return await action(); }
        catch (UnauthorizedAccessException) { return null; }
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(ApiEnvelope<T>.Ok(result.Value!));
        return Failure(ApiEnvelope<T>.Fail(result.Error ?? "操作失败。", result.Code));
    }

    private IActionResult ToActionResult(Result result)
    {
        if (result.IsSuccess) return Ok(ApiEnvelope<object>.Ok(new { success = true }));
        return Failure(ApiEnvelope<object>.Fail(result.Error ?? "操作失败。", result.Code));
    }

    private IActionResult Failure<T>(ApiEnvelope<T> envelope) => envelope.Code switch
    {
        "PERMISSION_DENIED" => StatusCode(StatusCodes.Status403Forbidden, envelope),
        "NOT_FOUND" or "OUT_OF_SCOPE" => NotFound(envelope),
        "CONCURRENCY_CONFLICT" or "DUPLICATE_EXTERNAL_CODE" or "INVALID_STATE" or "DRAFT_REQUIRED"
            or "PAPER_ARCHIVED" or "DUPLICATE_QUESTION" or "PAPER_NOT_READY" or "IDEMPOTENCY_CONFLICT"
            or "BANK_REQUIRED" or "INVALID_CATEGORY" or "INVALID_QUESTION_VERSION" or "CANDIDATE_POOL_TOO_LARGE"
            or "DUPLICATE_PAPER_MEMBER" or "PAPER_VERSION_IMMUTABLE" => Conflict(envelope),
        "IDEMPOTENCY_REPLAY" => Conflict(envelope),
        _ => BadRequest(envelope)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record ApiEnvelope<T>([property: JsonPropertyName("ok")] bool Success, T? Data, string? Error,
        string? Code, string TraceId, [property: JsonPropertyName("replayed")] bool Replayed = false)
    {
        public static ApiEnvelope<T> Ok(T data, bool replayed = false) => new(true, data, null, null, Activity.Current?.Id ?? "", replayed);
        public static ApiEnvelope<T> Fail(string error, string? code) => new(false, default, error, code, Activity.Current?.Id ?? "");
    }
}
