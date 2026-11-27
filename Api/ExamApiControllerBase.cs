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
using Microsoft.AspNetCore.Mvc;

namespace ExamOps.Api;

public abstract class ExamApiControllerBase(
    IUnitOfWork uow,
    AuditRepository audit,
    ICurrentUser currentUser) : ControllerBase
{
    protected async Task<IActionResult?> EnsurePermissionAsync(string permission)
    {
        await currentUser.LoadAsync();
        return currentUser.IsAuthenticated && currentUser.HasPermission(permission)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden,
                ApiEnvelope<object>.Fail("权限不足", "PERMISSION_DENIED"));
    }

    protected async Task<IActionResult?> EnsureAuthenticatedAsync()
    {
        await currentUser.LoadAsync();
        return currentUser.IsAuthenticated
            ? null
            : StatusCode(StatusCodes.Status401Unauthorized,
                ApiEnvelope<object>.Fail("请先登录", "UNAUTHENTICATED"));
    }

    protected async Task<(ApiAuditMetadata? Value, IActionResult? Error)> MetadataAsync(
        string? permission, bool keyRequired, string routeTemplate, object? payload)
    {
        var accessError = permission is null
            ? await EnsureAuthenticatedAsync()
            : await EnsurePermissionAsync(permission);
        if (accessError is not null) return (null, accessError);

        var key = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrEmpty(key))
            return keyRequired
                ? (null, BadRequest(ApiEnvelope<object>.Fail("缺少 Idempotency-Key", "IDEMPOTENCY_KEY_REQUIRED")))
                : (new ApiAuditMetadata(null, null, Request.Method, routeTemplate), null);
        if (key.Length > 128 || key.Any(char.IsWhiteSpace))
            return (null, BadRequest(ApiEnvelope<object>.Fail("Idempotency-Key 格式无效", "IDEMPOTENCY_KEY_INVALID")));

        var canonical = JsonSerializer.Serialize(new
        {
            path = Request.Path.Value,
            query = Request.QueryString.Value,
            payload
        }, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return (new ApiAuditMetadata(key, hash, Request.Method, routeTemplate), null);
    }

    protected async Task<IActionResult?> ReplayAsync<T>(ApiAuditMetadata? api, Func<T, T>? transform = null)
        where T : class
    {
        var (value, error) = await FindReplayAsync<T>(api);
        if (error is not null) return error;
        return value is null ? null : Ok(ApiEnvelope<T>.Ok(transform?.Invoke(value) ?? value, true));
    }

    protected async Task<(T? Value, IActionResult? Error)> FindReplayAsync<T>(ApiAuditMetadata? api)
        where T : class
    {
        if (api is null || string.IsNullOrWhiteSpace(api.IdempotencyKey) || currentUser.Id is not { } actorId)
            return (null, null);
        var existing = await audit.FindApiAuditAsync(uow.Db, actorId, api);
        if (existing is null) return (null, null);
        if (!string.Equals(existing.RequestHash, api.RequestHash, StringComparison.Ordinal))
            return (null, Conflict(ApiEnvelope<object>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT")));
        var value = JsonSerializer.Deserialize<T>(existing.ResultRefJson, JsonOptions)
            ?? throw new InvalidOperationException("幂等审计结果格式无效");
        return (value, null);
    }

    protected async Task<IActionResult?> ReplaySuccessAsync(ApiAuditMetadata? api)
    {
        var (value, error) = await FindReplayAsync<object>(api);
        if (error is not null) return error;
        return value is null ? null : Ok(ApiEnvelope<object>.Ok(new { success = true }, true));
    }

    protected static async Task<T?> ExecuteReadAsync<T>(Func<Task<T?>> action) where T : class
    {
        try
        {
            var result = await action();
            return result;
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    protected static async Task<T> ExecuteReadNonNullAsync<T>(Func<Task<T>> action) where T : class
    {
        try
        {
            var result = await action();
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("读取结果不应为空。");
        }
    }

    protected IActionResult Paged<T>(PagedResult<T> result) => Ok(ApiEnvelope<object>.Ok(new
    {
        items = result.Items,
        totalCount = result.TotalCount,
        pageIndex = result.PageIndex,
        pageSize = result.PageSize
    }));

    protected IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(ApiEnvelope<T>.Ok(result.Value!));
        return Failure(ApiEnvelope<T>.Fail(result.Error ?? "操作失败。", result.Code));
    }

    protected IActionResult ToActionResult(Result result)
    {
        if (result.IsSuccess) return Ok(ApiEnvelope<object>.Ok(new { success = true }));
        return Failure(ApiEnvelope<object>.Fail(result.Error ?? "操作失败。", result.Code));
    }

    protected IActionResult NotFoundResource(string code) =>
        NotFound(ApiEnvelope<object>.Fail("资源不存在或无权访问", code));

    private IActionResult Failure<T>(ApiEnvelope<T> envelope) => envelope.Code switch
    {
        "UNAUTHENTICATED" => StatusCode(StatusCodes.Status401Unauthorized, envelope),
        "PERMISSION_DENIED" => StatusCode(StatusCodes.Status403Forbidden, envelope),
        "NOT_FOUND" or "OUT_OF_SCOPE" => NotFound(envelope),
        "CONCURRENCY_CONFLICT" or "ROW_VERSION_CONFLICT" or "IDEMPOTENCY_CONFLICT" or "IDEMPOTENCY_REPLAY"
            or "DUPLICATE_EXAM_TASK_CODE" or "EXAM_TASK_CODE_EXISTS" or "DUPLICATE_TASK_STAFF" or "ASSIGNMENT_ALREADY_EXISTS"
            or "EXAM_TASK_NOT_DRAFT" or "EXAM_TASK_NOT_PUBLISHED" or "EXAM_TASK_NOT_CLOSED"
            or "EXAM_TASK_REVOKED" or "EXAM_WINDOW_INVALID" or "INVALID_STATE" or "INVALID_STATE_TRANSITION"
            or "ATTEMPT_STILL_IN_PROGRESS" or "EXAM_TASK_HAS_ASSIGNMENTS" or "PAPER_VERSION_NOT_PUBLISHED"
            or "USER_DISABLED" or "BATCH_NOT_RETRYABLE" or "ASSIGNMENT_WINDOW_NOT_OPEN"
            or "ASSIGNMENT_WINDOW_CLOSED" or "ATTEMPT_LIMIT_REACHED" or "ATTEMPT_ALREADY_SUBMITTED"
            or "RETAKE_COOLDOWN_ACTIVE" or "RETAKE_APPROVAL_REQUIRED" or "PAPER_SELECTION_FAILED"
            or "ATTEMPT_NOT_IN_PROGRESS" or "ATTEMPT_NOT_SUBMITTED" or "ATTEMPT_ALREADY_GRADED"
            or "ATTEMPT_NOT_GRADED" or "ATTEMPT_VOIDED" or "ANSWER_FROZEN"
            or "RESULT_ALREADY_PUBLISHED" or "RESULT_NOT_READY" or "RESULT_POLICY_BLOCKED" or "RESULT_NOT_PUBLISHED"
            or "OLD_SCORE_MISMATCH" or "NO_SCORE_CHANGE" => Conflict(envelope),
        _ => envelope.Code == "EXPORT_TOO_LARGE" ? StatusCode(StatusCodes.Status413PayloadTooLarge, envelope) : BadRequest(envelope)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed record ApiEnvelope<T>(
    [property: JsonPropertyName("ok")] bool Success,
    T? Data,
    string? Error,
    string? Code,
    string TraceId,
    [property: JsonPropertyName("replayed")] bool Replayed = false)
{
    public static ApiEnvelope<T> Ok(T data, bool replayed = false) =>
        new(true, data, null, null, Activity.Current?.Id ?? "", replayed);

    public static ApiEnvelope<T> Fail(string error, string? code) =>
        new(false, default, error, code, Activity.Current?.Id ?? "");
}
