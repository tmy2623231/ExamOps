using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamOps.BLL.Validators;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.BLL;

public sealed class ResultReleaseService(
    IUnitOfWork uow,
    ExamTaskRepository tasks,
    AuditRepository audit,
    OutboxRepository outbox,
    ExamIdempotencyService idempotency,
    DataScopeService scope,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<ResultReleaseService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<PublishResultOutput>> PublishAsync(long taskId, PublishResultInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.ExamResultPublish)) return Failure<PublishResultOutput>();
        var task = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (task is null) return Result<PublishResultOutput>.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, Permissions.ExamResultPublish))
            return Result<PublishResultOutput>.Fail("无权发布该任务成绩", "OUT_OF_SCOPE");
        PublishResultOutput? result = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async (ExamDbContext db) =>
            {
                result = await idempotency.FindReplayAsync<PublishResultOutput>(db, api);
                if (result is not null) return;
                await ExamLockService.LockTaskAsync(db, taskId);
                var current = await tasks.GetTrackedAsync(db, taskId) ?? throw new InvalidOperationException("NOT_FOUND");
                if (current.ResultStatus == "Published") throw new InvalidOperationException("RESULT_ALREADY_PUBLISHED");
                if (current.RowVersion != input.RowVersion) throw new InvalidOperationException("ROW_VERSION_CONFLICT");
                result = await PublishCoreAsync(db, current, currentUser.Id, IdempotencyKeyFactory.Create(currentUser, api, $"result-publish:{taskId}"), clock.UtcNow, false);
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, result), "exam_result.publish", "ExamTask", taskId,
                    after: result, eventKey: $"result-publish:{taskId}:{result.ReleaseNo}");
                await db.SaveChangesAsync();
                outbox.Add(db, "exam_result.published", "ExamTask", taskId, result,
                    $"result-published:{taskId}:{result.ReleaseNo}", clock.UtcNow);
                await db.SaveChangesAsync();
            });
            return Result<PublishResultOutput>.Ok(result!);
        }
        catch (ExamIdempotencyConflictException) { return Conflict<PublishResultOutput>(); }
        catch (InvalidOperationException ex) when (IsBusinessError(ex)) { return Result<PublishResultOutput>.Fail(MessageFor(ex.Message), ex.Message); }
        catch (DbUpdateConcurrencyException) { return Concurrency<PublishResultOutput>(); }
    }

    public async Task<Result<WithdrawResultOutput>> WithdrawAsync(long taskId, WithdrawResultInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.ExamResultPublish)) return Failure<WithdrawResultOutput>();
        var reason = ExamTaskValidator.ValidateReason(input.Reason);
        if (!reason.IsSuccess) return Result<WithdrawResultOutput>.Fail(reason.Error!, reason.Code);
        var task = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (task is null) return Result<WithdrawResultOutput>.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, Permissions.ExamResultPublish))
            return Result<WithdrawResultOutput>.Fail("无权撤回该任务成绩", "OUT_OF_SCOPE");
        WithdrawResultOutput? result = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async (ExamDbContext db) =>
            {
                result = await idempotency.FindReplayAsync<WithdrawResultOutput>(db, api);
                if (result is not null) return;
                await ExamLockService.LockTaskAsync(db, taskId);
                var current = await tasks.GetTrackedAsync(db, taskId) ?? throw new InvalidOperationException("NOT_FOUND");
                if (current.RowVersion != input.RowVersion) throw new InvalidOperationException("ROW_VERSION_CONFLICT");
                if (current.ResultStatus != "Published" || current.ResultReleaseId is null)
                    throw new InvalidOperationException("RESULT_NOT_PUBLISHED");
                var now = clock.UtcNow;
                current.ResultStatus = "Withdrawn";
                current.ResultWithdrawnAt = now;
                current.ResultWithdrawReason = input.Reason.Trim();
                current.UpdatedAt = now;
                current.RowVersion++;
                await db.SaveChangesAsync();
                result = new(current.ResultReleaseId.Value, now, current.RowVersion);
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, result), "exam_result.withdraw", "ExamTask", taskId,
                    after: result, reason: input.Reason.Trim(), eventKey: $"result-withdraw:{taskId}:{current.ResultReleaseId}:{now.Ticks}");
                await db.SaveChangesAsync();
                outbox.Add(db, "exam_result.withdrawn", "ExamTask", taskId, result,
                    $"result-withdrawn:{taskId}:{current.ResultReleaseId}:{now.Ticks}", now);
                await db.SaveChangesAsync();
            });
            return Result<WithdrawResultOutput>.Ok(result!);
        }
        catch (ExamIdempotencyConflictException) { return Conflict<WithdrawResultOutput>(); }
        catch (InvalidOperationException ex) when (IsBusinessError(ex)) { return Result<WithdrawResultOutput>.Fail(MessageFor(ex.Message), ex.Message); }
        catch (DbUpdateConcurrencyException) { return Concurrency<WithdrawResultOutput>(); }
    }

    /// <summary>Runs after the grading/closing transaction commits; failure cannot undo a submitted attempt.</summary>
    public async Task<PublishResultOutput?> TryAutoPublishAsync(long taskId)
    {
        PublishResultOutput? result = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async (ExamDbContext db) =>
            {
                var now = clock.UtcNow;
                await ExamLockService.LockTaskAsync(db, taskId);
                var task = await tasks.GetTrackedAsync(db, taskId);
                if (task is null || task.ResultStatus != "Unpublished"
                    || task.ResultReleasePolicy is not ("Immediate" or "AfterClose")) return;
                if (task.ResultReleasePolicy == "AfterClose" && task.Status != "Closed") return;
                result = await PublishCoreAsync(db, task, null, $"auto-result:{taskId}:{task.RowVersion}", now, true);
                await audit.WriteAsync(db, clock, new AuditContext(null, "System", null, null, null), "exam_result.auto_publish",
                    "ExamTask", taskId, after: result, eventKey: $"result-publish:{taskId}:{result.ReleaseNo}");
                await db.SaveChangesAsync();
                outbox.Add(db, "exam_result.published", "ExamTask", taskId, result,
                    $"result-published:{taskId}:{result.ReleaseNo}", now);
                await db.SaveChangesAsync();
            });
        }
        catch (InvalidOperationException ex) when (IsBusinessError(ex))
        {
            logger.LogDebug("成绩尚未满足自动发布条件。TaskId={TaskId}, Code={Code}", taskId, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "自动发布成绩失败；答卷提交结果保留。TaskId={TaskId}", taskId);
        }
        return result;
    }

    public async Task<int> TryAutoPublishDueAsync()
    {
        var taskIds = await uow.Db.ExamTasks.AsNoTracking().Where(x => x.ResultStatus == "Unpublished"
            && (x.ResultReleasePolicy == "Immediate" || (x.ResultReleasePolicy == "AfterClose" && x.Status == "Closed")))
            .OrderBy(x => x.Id).Take(100).Select(x => x.Id).ToListAsync();
        foreach (var taskId in taskIds)
            if (await TryAutoPublishAsync(taskId) is not null) return 1;
        return 0;
    }

    public async Task<IReadOnlyList<ResultPublicationTaskRow>> GetTasksAsync()
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.ExamResultPublish))
            throw new InvalidOperationException("PERMISSION_DENIED");
        var departments = await scope.GetAccessibleDepartmentIdsAsync(Permissions.ExamResultPublish);
        var assigned = await scope.GetAssignedTaskIdsAsync(Permissions.ExamResultPublish);
        var query = uow.Db.ExamTasks.AsNoTracking()
            .Where(x => departments == null || departments.Contains(x.DepartmentId) || assigned.Contains(x.Id))
            .OrderBy(x => x.Id);
        return await query.Select(x => new ResultPublicationTaskRow(x.Id, x.Title, x.DepartmentId,
            x.Status, x.ResultStatus, x.ScorePolicy, x.ResultReleasePolicy, x.RowVersion, x.ResultReleaseId,
            x.ResultPublishedAt, x.ResultWithdrawnAt, x.ResultWithdrawReason)).ToListAsync();
    }

    public async Task<ResultReleaseItem?> GetPublishedItemAsync(long assignmentId, long userId)
    {
        var taskId = await uow.Db.ExamAssignments.AsNoTracking().Where(x => x.Id == assignmentId && x.UserId == userId)
            .Select(x => (long?)x.TaskId).FirstOrDefaultAsync();
        if (taskId is null) return null;
        var published = await uow.Db.ExamTasks.AsNoTracking().AnyAsync(x => x.Id == taskId && x.ResultStatus == "Published");
        if (!published) return null;
        return await uow.Db.ResultReleaseItems.AsNoTracking().FirstOrDefaultAsync(x =>
            x.ReleaseId == uow.Db.ExamTasks.Where(t => t.Id == taskId).Select(t => t.ResultReleaseId).FirstOrDefault()
            && x.AssignmentId == assignmentId && x.UserId == userId);
    }

    public async Task<IReadOnlyList<ResultReleaseHistoryRow>> GetHistoryAsync(long taskId)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.ExamResultPublish))
            throw new InvalidOperationException("PERMISSION_DENIED");
        var task = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (task is null || !await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, Permissions.ExamResultPublish))
            throw new InvalidOperationException("NOT_FOUND");
        return await (from release in uow.Db.ResultReleases.AsNoTracking()
                      join user in uow.Db.Users.AsNoTracking() on release.PublishedBy equals user.Id into users
                      from user in users.DefaultIfEmpty()
                      where release.TaskId == taskId
                      orderby release.ReleaseNo descending
                      select new ResultReleaseHistoryRow(release.Id, release.ReleaseNo, release.ScorePolicy,
                          release.ResultReleasePolicy, release.PayloadSha256, release.PublishedAt, release.PublishedBy,
                          user.DisplayName,
                          uow.Db.ResultReleaseItems.Count(x => x.ReleaseId == release.Id),
                          uow.Db.ResultReleaseItems.Count(x => x.ReleaseId == release.Id && x.FinalScore != null)))
            .ToListAsync();
    }

    private async Task<PublishResultOutput> PublishCoreAsync(ExamDbContext db, ExamTask task, long? actorId,
        string releaseKey, DateTime now, bool automatic)
    {
        if (task.ResultStatus == "Published") throw new InvalidOperationException("RESULT_ALREADY_PUBLISHED");
        if (task.Status is not ("Published" or "Closed")) throw new InvalidOperationException("RESULT_NOT_READY");
        if (!automatic && task.ResultReleasePolicy == "AfterClose" && task.Status != "Closed")
            throw new InvalidOperationException("RESULT_POLICY_BLOCKED");
        var assignments = await db.ExamAssignments.AsNoTracking().Where(x => x.TaskId == task.Id)
            .OrderBy(x => x.Id).ToListAsync();
        if (assignments.Any(x => x.Status is "Pending" or "Available" or "InProgress" or "Submitted"))
            throw new InvalidOperationException("RESULT_NOT_READY");
        var assignmentIds = assignments.Select(x => x.Id).ToArray();
        var attempts = await db.ExamAttempts.AsNoTracking().Where(x => assignmentIds.Contains(x.AssignmentId)).ToListAsync();
        if (attempts.Any(x => x.Status is "InProgress" or "Submitted" or "AutoSubmitted"
            || (x.Status == "Graded" && (x.Score is null || x.GradedAt is null
                || x.Score < 0 || x.Score > x.MaxScore || x.MaxScore > 9999.99m))))
            throw new InvalidOperationException("RESULT_NOT_READY");
        var releaseNo = await db.ResultReleases.AsNoTracking().Where(x => x.TaskId == task.Id)
            .Select(x => (int?)x.ReleaseNo).DefaultIfEmpty().MaxAsync() + 1 ?? 1;
        var items = new List<ResultReleaseItem>();
        var payloadItems = new List<object>();
        foreach (var assignment in assignments)
        {
            var valid = attempts.Where(x => x.AssignmentId == assignment.Id && x.Status == "Graded" && x.Score is not null)
                .Select(x => new ScoredAttempt(x.Id, x.AttemptNo, x.Score!.Value, x.MaxScore, x.AutoScore ?? x.Score.Value, x.GradedAt ?? now))
                .ToArray();
            var final = ExamAlgorithms.CalculateFinalScore(task.ScorePolicy, task.PassScore, valid);
            var source = JsonSerializer.Serialize(new
            {
                method = final.Method, validAttemptIds = final.ValidAttemptIds,
                attempts = valid.OrderBy(x => x.AttemptNo).Select(x => new
                { attemptId = x.AttemptId, attemptNo = x.AttemptNo, autoScore = x.AutoScore, currentScore = x.Score, x.MaxScore, x.GradedAt })
            }, JsonOptions);
            items.Add(new ResultReleaseItem
            {
                TaskId = task.Id, AssignmentId = assignment.Id, UserId = assignment.UserId,
                DepartmentIdSnapshot = assignment.DepartmentIdSnapshot, AttemptId = final.AttemptId,
                FinalScore = final.FinalScore, MaxScore = final.MaxScore, Passed = final.Passed, SourceJson = source
            });
            payloadItems.Add(new
            {
                assignmentId = assignment.Id, userId = assignment.UserId, departmentId = assignment.DepartmentIdSnapshot,
                attemptId = final.AttemptId, finalScore = final.FinalScore, maxScore = final.MaxScore, passed = final.Passed,
                source = JsonSerializer.Deserialize<JsonElement>(source)
            });
        }
        var scored = items.Count(x => x.FinalScore is not null);
        var passed = items.Count(x => x.Passed == true);
        var summary = new { assignmentCount = items.Count, scoredCount = scored, unscoredCount = items.Count - scored,
            passedCount = passed, passRate = scored == 0 ? (decimal?)null : decimal.Round((decimal)passed / scored, 4, MidpointRounding.AwayFromZero) };
        var canonical = JsonSerializer.Serialize(new
        {
            taskId = task.Id, releaseNo, scorePolicy = task.ScorePolicy, passScore = task.PassScore,
            resultReleasePolicy = task.ResultReleasePolicy, publishedAt = now, summary, items = payloadItems
        }, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var release = new ResultRelease
        {
            TaskId = task.Id, ReleaseNo = releaseNo, ScorePolicy = task.ScorePolicy, PassScore = task.PassScore,
            ResultReleasePolicy = task.ResultReleasePolicy, SummaryJson = JsonSerializer.Serialize(summary, JsonOptions),
            PayloadSha256 = hash, PublishedBy = actorId, PublishedAt = now, IdempotencyKey = releaseKey
        };
        db.ResultReleases.Add(release);
        await db.SaveChangesAsync();
        foreach (var item in items) item.ReleaseId = release.Id;
        db.ResultReleaseItems.AddRange(items);
        task.ResultReleaseId = release.Id;
        task.ResultStatus = "Published";
        task.ResultPublishedAt = now;
        task.ResultWithdrawnAt = null;
        task.ResultWithdrawReason = null;
        task.UpdatedAt = now;
        task.RowVersion++;
        await db.SaveChangesAsync();
        return new(release.Id, release.ReleaseNo, hash, now, task.RowVersion);
    }

    private static bool IsBusinessError(InvalidOperationException ex)
        => ex.Message is "NOT_FOUND" or "ROW_VERSION_CONFLICT" or "RESULT_ALREADY_PUBLISHED" or "RESULT_NOT_READY"
            or "RESULT_POLICY_BLOCKED" or "RESULT_NOT_PUBLISHED";
    private Result<T> Failure<T>() => currentUser.IsAuthenticated
        ? Result<T>.Fail("权限不足", "PERMISSION_DENIED") : Result<T>.Fail("请先登录", "UNAUTHENTICATED");
    private static Result<T> Conflict<T>() => Result<T>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private static Result<T> Concurrency<T>() => Result<T>.Fail("数据已被其他操作修改，请刷新后重试", "ROW_VERSION_CONFLICT");
    private static string MessageFor(string code) => code switch
    {
        "NOT_FOUND" => "考试任务不存在", "ROW_VERSION_CONFLICT" => "数据已被其他操作修改，请刷新后重试",
        "RESULT_ALREADY_PUBLISHED" => "成绩已发布", "RESULT_NOT_READY" => "仍有未完成答卷或可继续考试的安排",
        "RESULT_POLICY_BLOCKED" => "当前成绩公布策略不允许发布", "RESULT_NOT_PUBLISHED" => "成绩当前未发布",
        _ => "操作失败"
    };
}
