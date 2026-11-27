using System.Text.Json;
using ExamOps.BLL.Validators;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.BLL;

public sealed class ExamReviewService(
    IUnitOfWork uow,
    ExamReviewRepository reviews,
    AttemptRepository attempts,
    ExamTaskRepository tasks,
    AuditRepository audit,
    OutboxRepository outbox,
    ExamIdempotencyService idempotency,
    DataScopeService scope,
    ICurrentUser currentUser,
    IClock clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<ReviewQueueRow>> GetQueueAsync(ExamReviewQuery query)
    {
        await currentUser.LoadAsync();
        Require(Permissions.ExamAttemptRead);
        var departments = await scope.GetAccessibleDepartmentIdsAsync(Permissions.ExamAttemptRead);
        var assigned = await scope.GetAssignedTaskIdsAsync(Permissions.ExamAttemptRead);
        var normalized = query with
        {
            ReviewType = string.IsNullOrWhiteSpace(query.ReviewType) ? null : query.ReviewType.Trim(),
            PageIndex = Math.Max(1, query.PageIndex), PageSize = Math.Clamp(query.PageSize, 1, 100)
        };
        if (normalized.ReviewType is not (null or ReviewTypes.FormatAnomaly or ReviewTypes.Dispute or ReviewTypes.VoidRequest))
            return new PagedResult<ReviewQueueRow>([], 0, normalized.PageIndex, normalized.PageSize);
        return await reviews.GetQueueAsync(uow.Db, departments, assigned, normalized, normalized.PageIndex, normalized.PageSize);
    }

    public async Task<ReviewQueueRow?> GetDetailAsync(long attemptId)
    {
        await currentUser.LoadAsync();
        Require(Permissions.ExamAttemptRead);
        var context = await reviews.GetContextAsync(uow.Db, attemptId);
        if (context is null || !await scope.CanAccessTaskAsync(context.TaskId, context.DepartmentId, Permissions.ExamAttemptRead))
            return null;
        return await reviews.GetDetailAsync(uow.Db, attemptId);
    }

    public async Task<IReadOnlyList<AttemptEventRow>> GetEventsAsync(long attemptId)
    {
        await currentUser.LoadAsync();
        Require(Permissions.ExamAttemptRead);
        var context = await reviews.GetContextAsync(uow.Db, attemptId);
        if (context is null || !await scope.CanAccessTaskAsync(context.TaskId, context.DepartmentId, Permissions.ExamAttemptRead))
            throw new InvalidOperationException("NOT_FOUND");
        return await reviews.GetEventsAsync(uow.Db, attemptId);
    }

    public async Task<Result<ReviewEventResult>> AddReviewEventAsync(long attemptId, ReviewEventInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.ExamAttemptGrade)) return Failure<ReviewEventResult>();
        if (input.ReviewType is not (ReviewTypes.FormatAnomaly or ReviewTypes.Dispute or ReviewTypes.VoidRequest))
            return Result<ReviewEventResult>.Fail("复核类型无效", "VALIDATION_ERROR");
        var validation = ExamTaskValidator.ValidateReason(input.Reason);
        if (!validation.IsSuccess) return Result<ReviewEventResult>.Fail(validation.Error!, validation.Code);
        var context = await reviews.GetContextAsync(uow.Db, attemptId);
        if (context is null || !await scope.CanAccessTaskAsync(context.TaskId, context.DepartmentId, Permissions.ExamAttemptGrade))
            return Result<ReviewEventResult>.Fail("答卷不存在或无权访问", "NOT_FOUND");
        ReviewEventResult? result = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async (ExamDbContext db) =>
            {
                result = await idempotency.FindReplayAsync<ReviewEventResult>(db, api);
                if (result is not null) return;
                await ExamLockService.LockTaskAsync(db, context.TaskId);
                await ExamLockService.LockAssignmentAsync(db, context.AssignmentId);
                await ExamLockService.LockAttemptAsync(db, attemptId);
                var attempt = await attempts.GetTrackedAsync(db, attemptId);
                var task = await tasks.GetTrackedAsync(db, context.TaskId);
                if (attempt is null || task is null) { result = null; throw new InvalidOperationException("NOT_FOUND"); }
                if (task.ResultStatus == "Published") throw new InvalidOperationException("RESULT_ALREADY_PUBLISHED");
                if (attempt.Status is not ("Submitted" or "AutoSubmitted" or "Graded")) throw new InvalidOperationException("ATTEMPT_NOT_SUBMITTED");
                if (!await reviews.HasObjectiveAnswersAsync(db, attemptId)) throw new InvalidOperationException("OBJECTIVE_QUESTION_REQUIRED");
                var now = clock.UtcNow;
                var eventKey = RequiredKey(api, $"attempt-review:{attemptId}");
                var entity = new AttemptEvent
                {
                    AttemptId = attemptId, ActorId = currentUser.Id, EventType = input.ReviewType,
                    PayloadJson = Serialize(new { input.ReviewType, Reason = input.Reason.Trim() }),
                    OccurredAt = now, EventKey = eventKey
                };
                attempts.AddEvent(db, entity);
                await db.SaveChangesAsync();
                result = new(entity.Id, attemptId, input.ReviewType, now);
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, result), "exam_attempt.review_event",
                    "ExamAttempt", attemptId, after: new { input.ReviewType, Reason = input.Reason.Trim() },
                    reason: input.Reason.Trim(), eventKey: eventKey);
                outbox.Add(db, "exam_attempt.review_recorded", "ExamAttempt", attemptId,
                    new { attemptId, taskId = context.TaskId, input.ReviewType, eventId = entity.Id }, eventKey, now);
                await db.SaveChangesAsync();
            });
            return Result<ReviewEventResult>.Ok(result!);
        }
        catch (ExamIdempotencyConflictException) { return Conflict<ReviewEventResult>(); }
        catch (InvalidOperationException ex) when (ex.Message is "NOT_FOUND" or "OBJECTIVE_QUESTION_REQUIRED"
            || ex.Message.StartsWith("RESULT_") || ex.Message.StartsWith("ATTEMPT_"))
        { return Result<ReviewEventResult>.Fail(MessageFor(ex.Message), ex.Message); }
        catch (DbUpdateConcurrencyException) { return Concurrency<ReviewEventResult>(); }
    }

    public async Task<Result<AdjustScoreResult>> AdjustScoreAsync(long attemptId, AdjustScoreInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.ExamAttemptAdjustScore)) return Failure<AdjustScoreResult>();
        if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length < 10)
            return Result<AdjustScoreResult>.Fail("调分原因至少需要 10 个字符", "REASON_REQUIRED");
        if (input.OldScore is < 0 or > 9999.99m || input.NewScore is < 0 or > 9999.99m)
            return Result<AdjustScoreResult>.Fail("分数必须在 0–9999.99 之间", "SCORE_OUT_OF_RANGE");
        if (input.OldScore == input.NewScore)
            return Result<AdjustScoreResult>.Fail("新旧分数不能相同", "NO_SCORE_CHANGE");
        var context = await reviews.GetContextAsync(uow.Db, attemptId);
        if (context is null || !await scope.CanAccessTaskAsync(context.TaskId, context.DepartmentId, Permissions.ExamAttemptAdjustScore))
            return Result<AdjustScoreResult>.Fail("答卷不存在或无权访问", "NOT_FOUND");
        AdjustScoreResult? result = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async (ExamDbContext db) =>
            {
                result = await idempotency.FindReplayAsync<AdjustScoreResult>(db, api);
                if (result is not null) return;
                await ExamLockService.LockTaskAsync(db, context.TaskId);
                await ExamLockService.LockAssignmentAsync(db, context.AssignmentId);
                await ExamLockService.LockAttemptAsync(db, attemptId);
                var attempt = await attempts.GetTrackedAsync(db, attemptId);
                var task = await tasks.GetTrackedAsync(db, context.TaskId);
                if (attempt is null || task is null) throw new InvalidOperationException("NOT_FOUND");
                if (task.ResultStatus == "Published") throw new InvalidOperationException("RESULT_ALREADY_PUBLISHED");
                if (attempt.RowVersion != input.RowVersion) throw new InvalidOperationException("ROW_VERSION_CONFLICT");
                if (attempt.Status != "Graded") throw new InvalidOperationException("ATTEMPT_NOT_GRADED");
                if (attempt.Score != input.OldScore) throw new InvalidOperationException("OLD_SCORE_MISMATCH");
                if (input.NewScore > attempt.MaxScore) throw new InvalidOperationException("SCORE_OUT_OF_RANGE");
                var key = RequiredKey(api, $"attempt-score:{attemptId}");
                var existing = await reviews.GetAdjustmentByKeyAsync(db, key);
                if (existing is not null)
                {
                    result = new(existing.Id, attemptId, existing.NewScore, attempt.AutoScore ?? 0, attempt.Status, attempt.RowVersion, true);
                    return;
                }
                var now = clock.UtcNow;
                var adjustment = new ScoreAdjustment
                {
                    AttemptId = attemptId, OldScore = input.OldScore, NewScore = input.NewScore,
                    Reason = input.Reason.Trim(), AdjustedBy = currentUser.Id!.Value, IdempotencyKey = key, CreatedAt = now
                };
                reviews.AddAdjustment(db, adjustment);
                // SQL trigger validates old_score against the still-current attempt score.
                await db.SaveChangesAsync();
                var before = new { attempt.Score, attempt.AutoScore, attempt.RowVersion };
                attempt.Score = input.NewScore;
                attempt.RowVersion++;
                attempts.AddEvent(db, NewEvent(attemptId, currentUser.Id, "ScoreAdjusted",
                    new { adjustment.Id, input.OldScore, input.NewScore, adjustment.Reason }, key, now));
                result = new(adjustment.Id, attemptId, attempt.Score ?? input.NewScore, attempt.AutoScore ?? 0, attempt.Status, attempt.RowVersion);
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, result), "exam_attempt.adjust_score",
                    "ExamAttempt", attemptId, before, after: new { attempt.Score, attempt.AutoScore, adjustment.Id },
                    reason: input.Reason.Trim(), eventKey: key);
                outbox.Add(db, "exam_attempt.score_adjusted", "ExamAttempt", attemptId,
                    new { attemptId, taskId = context.TaskId, adjustmentId = adjustment.Id, input.OldScore, input.NewScore }, key, now);
                await db.SaveChangesAsync();
            });
            return Result<AdjustScoreResult>.Ok(result!);
        }
        catch (ExamIdempotencyConflictException) { return Conflict<AdjustScoreResult>(); }
        catch (InvalidOperationException ex) when (ex.Message is "NOT_FOUND" or "RESULT_ALREADY_PUBLISHED" or "ROW_VERSION_CONFLICT"
            or "ATTEMPT_NOT_GRADED" or "OLD_SCORE_MISMATCH" or "SCORE_OUT_OF_RANGE")
        { return Result<AdjustScoreResult>.Fail(MessageFor(ex.Message), ex.Message); }
        catch (DbUpdateConcurrencyException) { return Concurrency<AdjustScoreResult>(); }
    }

    public async Task<PagedResult<ScoreAdjustmentRow>> GetAdjustmentsAsync(long attemptId, int pageIndex, int pageSize)
    {
        await currentUser.LoadAsync();
        Require(Permissions.ExamAttemptAdjustScore);
        var context = await reviews.GetContextAsync(uow.Db, attemptId);
        if (context is null || !await scope.CanAccessTaskAsync(context.TaskId, context.DepartmentId, Permissions.ExamAttemptAdjustScore))
            throw new InvalidOperationException("NOT_FOUND");
        return await reviews.GetAdjustmentsPagedAsync(uow.Db, attemptId, Math.Max(1, pageIndex), Math.Clamp(pageSize, 1, 100));
    }

    private void Require(string permission)
    {
        if (!currentUser.IsAuthenticated) throw new InvalidOperationException("UNAUTHENTICATED");
        if (!currentUser.HasPermission(permission)) throw new InvalidOperationException("PERMISSION_DENIED");
    }

    private Result<T> Failure<T>() => currentUser.IsAuthenticated
        ? Result<T>.Fail("权限不足", "PERMISSION_DENIED") : Result<T>.Fail("请先登录", "UNAUTHENTICATED");
    private Result<T> Conflict<T>() => Result<T>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private Result<T> Concurrency<T>() => Result<T>.Fail("数据已被其他操作修改，请刷新后重试", "ROW_VERSION_CONFLICT");

    private static AttemptEvent NewEvent(long attemptId, long? actorId, string type, object payload, string key, DateTime now)
        => new() { AttemptId = attemptId, ActorId = actorId, EventType = type, PayloadJson = Serialize(payload), OccurredAt = now, EventKey = key };

    private static string RequiredKey(ApiAuditMetadata? api, string prefix)
        => string.IsNullOrWhiteSpace(api?.IdempotencyKey) ? $"{prefix}:{Guid.NewGuid():N}" : api.IdempotencyKey;

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string MessageFor(string code) => code switch
    {
        "RESULT_ALREADY_PUBLISHED" => "成绩已发布，请先撤回成绩后再操作",
        "ATTEMPT_NOT_SUBMITTED" => "只有已交卷答卷可以复核",
        "OBJECTIVE_QUESTION_REQUIRED" => "仅支持客观题复核",
        "ATTEMPT_NOT_GRADED" => "只有已自动判分的答卷可以调分",
        "ROW_VERSION_CONFLICT" => "数据已被其他操作修改，请刷新后重试",
        "OLD_SCORE_MISMATCH" => "旧分数与当前成绩不一致，请刷新后重试",
        "SCORE_OUT_OF_RANGE" => "新分数超出本答卷满分范围",
        "NOT_FOUND" => "答卷不存在",
        _ => "操作失败"
    };
}
