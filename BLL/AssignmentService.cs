using System.Text.Json;
using ExamOps.BLL.Validators;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using ExamOps.Model.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExamOps.BLL;

public sealed class AssignmentService(
    IUnitOfWork uow,
    AssignmentRepository assignments,
    ExamTaskRepository tasks,
    ExamAttemptService attemptService,
    DataScopeService scope,
    AuditRepository audit,
    OutboxRepository outbox,
    ExamIdempotencyService idempotency,
    ICurrentUser currentUser,
    IClock clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<ExamAssignmentRow>> GetPagedAsync(AssignmentQuery query)
    {
        await currentUser.LoadAsync();
        Require(Permissions.ExamAssignmentRead);
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.ExamAssignmentRead);
        var assigned = await scope.GetAssignedTaskIdsAsync(Permissions.ExamAssignmentRead);
        return await assignments.GetPagedAsync(uow.Db, query with { PageIndex = Math.Max(1, query.PageIndex), PageSize = Math.Clamp(query.PageSize, 1, 100) }, allowed, assigned);
    }

    public async Task<ExamAssignmentRow?> GetRowAsync(long assignmentId)
    {
        await currentUser.LoadAsync();
        Require(Permissions.ExamAssignmentRead);
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.ExamAssignmentRead);
        var assigned = await scope.GetAssignedTaskIdsAsync(Permissions.ExamAssignmentRead);
        return await assignments.GetRowAsync(uow.Db, assignmentId, allowed, assigned);
    }

    public async Task<AssignmentBatchDetail?> GetBatchDetailAsync(long batchId, int pageIndex, int pageSize)
    {
        await currentUser.LoadAsync();
        Require(Permissions.ExamAssignmentRead);
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.ExamAssignmentRead);
        var assigned = await scope.GetAssignedTaskIdsAsync(Permissions.ExamAssignmentRead);
        return await assignments.GetBatchDetailAsync(uow.Db, batchId, Math.Max(1, pageIndex), Math.Clamp(pageSize, 1, 100), allowed, assigned);
    }

    public async Task<Result<AssignmentPreview>> PreviewAsync(long taskId, AssignmentTargetInput target)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamAssignmentCreate)) return Result<AssignmentPreview>.Fail("权限不足", "PERMISSION_DENIED");
        var task = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (task is null) return Result<AssignmentPreview>.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, Permissions.ExamAssignmentCreate)) return Result<AssignmentPreview>.Fail("无权操作该考试任务", "OUT_OF_SCOPE");
        if (task.Status != ExamTaskStatus.Published) return Result<AssignmentPreview>.Fail("只能下发已发布任务", "EXAM_TASK_NOT_PUBLISHED");
        var department = await uow.Db.Departments.AsNoTracking().FirstAsync(x => x.Id == task.DepartmentId);
        var resolved = await assignments.ResolveTargetsAsync(uow.Db, taskId, department.Path, target);
        var rows = resolved.Select(user =>
        {
            if (user.ExistingAssignmentId is not null && user.ExistingStatus != "Revoked")
                return new AssignmentPreviewRow(user.UserId, user.DisplayName, user.DepartmentName, "Skipped", "已有有效安排", user.ExistingAssignmentId);
            if (user.UserStatus != "Active")
                return new AssignmentPreviewRow(user.UserId, user.DisplayName, user.DepartmentName, "Failed", "用户已停用", user.ExistingAssignmentId);
            if (user.ExistingStatus == "Revoked" && !target.ReissueRevoked)
                return new AssignmentPreviewRow(user.UserId, user.DisplayName, user.DepartmentName, "Skipped", "旧安排已撤回；如需重发请显式选择重新下发", user.ExistingAssignmentId);
            return new AssignmentPreviewRow(user.UserId, user.DisplayName, user.DepartmentName, "Eligible",
                user.ExistingStatus == "Revoked" ? "将创建重新下发安排" : null, user.ExistingAssignmentId);
        }).ToArray();
        return Result<AssignmentPreview>.Ok(new AssignmentPreview(taskId, task.RowVersion, rows.Length, rows,
            rows.Count(x => x.Outcome == "Eligible"), rows.Count(x => x.Outcome == "Skipped"), rows.Count(x => x.Outcome == "Failed")));
    }

    public async Task<Result<AssignmentMutationResult>> AssignAsync(long taskId, AssignExamInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamAssignmentCreate)) return Failure<AssignmentMutationResult>();
        if (input.Target.UserIds.Count == 0 && input.Target.DepartmentIds.Count == 0)
            return Result<AssignmentMutationResult>.Fail("请至少选择一个用户或部门", "VALIDATION_ERROR");
        var task = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (task is null) return Result<AssignmentMutationResult>.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, Permissions.ExamAssignmentCreate)) return Result<AssignmentMutationResult>.Fail("无权操作该考试任务", "OUT_OF_SCOPE");
        AssignmentMutationResult? replay = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                replay = await idempotency.FindReplayAsync<AssignmentMutationResult>(db, api);
                if (replay is not null) return;
                await ExamLockService.LockTaskAsync(db, taskId);
                var tracked = await tasks.GetTrackedAsync(db, taskId);
                if (tracked is null) throw new InvalidOperationException("NOT_FOUND");
                if (tracked.RowVersion != input.TaskRowVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
                if (tracked.Status != ExamTaskStatus.Published) throw new InvalidOperationException("EXAM_TASK_NOT_PUBLISHED");
                var department = await db.Departments.AsNoTracking().FirstAsync(x => x.Id == tracked.DepartmentId);
                var targets = await assignments.ResolveTargetsAsync(db, taskId, department.Path, input.Target);
                if (targets.Count == 0) throw new InvalidOperationException("NO_TARGETS");
                var now = clock.UtcNow;
                AssignmentBatchRow[] rows;
                var batch = new AssignmentBatch
                {
                    TaskId = taskId, IdempotencyKey = RequiredKey(api), TargetJson = Serialize(input.Target), Status = "Pending",
                    TotalCount = targets.Count, CreatedBy = ActorId(), CreatedAt = now, RowVersion = 1
                };
                await assignments.AddBatchAsync(db, batch, []);
                await db.SaveChangesAsync();
                batch.Status = "Running"; batch.StartedAt = now; batch.RowVersion++;
                rows = targets.Select((x, index) => new AssignmentBatchRow
                {
                    BatchId = batch.Id, RowNo = index + 1, UserId = x.UserId,
                    TargetJson = Serialize(new { x.UserId, x.DepartmentId, x.DepartmentPath }), Status = "Pending",
                    ErrorJson = "[]", CreatedAt = now, RowVersion = 1
                }).ToArray();
                await db.AssignmentBatchRows.AddRangeAsync(rows);
                await db.SaveChangesAsync();
                await ProcessBatchAsync(db, batch, rows, tracked, input.Target, now);
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { batchId = batch.Id, taskId }), "exam_assignment.batch_create", "AssignmentBatch", batch.Id,
                    after: new { batch.Status, batch.TotalCount, batch.SuccessCount, batch.FailedCount, batch.SkippedCount });
                await db.SaveChangesAsync();
                replay = new(batch.Id, taskId);
            });
            return Result<AssignmentMutationResult>.Ok(replay!);
        }
        catch (ExamIdempotencyConflictException) { return Conflict<AssignmentMutationResult>(); }
        catch (InvalidOperationException ex) { return Result<AssignmentMutationResult>.Fail(MessageFor(ex.Message), ex.Message); }
        catch (DbUpdateConcurrencyException) { return Concurrency<AssignmentMutationResult>(); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return Result<AssignmentMutationResult>.Fail("批次或安排已存在，请刷新后重试", "ASSIGNMENT_ALREADY_EXISTS"); }
    }

    public async Task<Result<AssignmentMutationResult>> RetryBatchAsync(long batchId, long rowVersion, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamAssignmentCreate)) return Failure<AssignmentMutationResult>();
        var batchRow = await assignments.GetBatchTrackedAsync(uow.Db, batchId);
        if (batchRow is null) return Result<AssignmentMutationResult>.Fail("批次不存在", "NOT_FOUND");
        var detailTask = await tasks.GetNoTrackingAsync(uow.Db, batchRow.TaskId);
        if (detailTask is null) return Result<AssignmentMutationResult>.Fail("批次不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(detailTask.Id, detailTask.DepartmentId, Permissions.ExamAssignmentCreate))
            return OutOfScope<AssignmentMutationResult>();
        AssignmentMutationResult? replay = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                replay = await idempotency.FindReplayAsync<AssignmentMutationResult>(db, api);
                if (replay is not null) return;
                await ExamLockService.LockTaskAsync(db, batchRow.TaskId);
                var batch = await assignments.GetBatchTrackedAsync(db, batchId);
                if (batch is null) throw new InvalidOperationException("NOT_FOUND");
                if (batch.RowVersion != rowVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
                if (batch.Status is not ("Failed" or "PartiallyFailed")) throw new InvalidOperationException("BATCH_NOT_RETRYABLE");
                var task = await tasks.GetTrackedAsync(db, batch.TaskId) ?? throw new InvalidOperationException("NOT_FOUND");
                if (task.Status != ExamTaskStatus.Published) throw new InvalidOperationException("EXAM_TASK_NOT_PUBLISHED");
                var rows = (await assignments.GetBatchRowsTrackedAsync(db, batchId)).ToArray();
                batch.Status = "Running"; batch.FinishedAt = null; batch.RowVersion++;
                batch.SuccessCount = 0; batch.FailedCount = 0; batch.SkippedCount = 0;
                // Failed 行按 SQL 状态机直接进入 Processing，不绕过审计语义回退为 Pending。
                await db.SaveChangesAsync();
                var target = JsonSerializer.Deserialize<AssignmentTargetInput>(batch.TargetJson, JsonOptions) ?? new([], [], false, false, false);
                await ProcessBatchAsync(db, batch, rows, task, target, clock.UtcNow);
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { batchId }), "exam_assignment.batch_retry", "AssignmentBatch", batchId,
                    after: new { batch.Status, batch.SuccessCount, batch.FailedCount, batch.SkippedCount });
                await db.SaveChangesAsync(); replay = new(batchId, batch.TaskId);
            });
            return Result<AssignmentMutationResult>.Ok(replay!);
        }
        catch (ExamIdempotencyConflictException) { return Conflict<AssignmentMutationResult>(); }
        catch (InvalidOperationException ex) { return Result<AssignmentMutationResult>.Fail(MessageFor(ex.Message), ex.Message); }
        catch (DbUpdateConcurrencyException) { return Concurrency<AssignmentMutationResult>(); }
    }

    public async Task<Result> RevokeAsync(long assignmentId, RevokeAssignmentInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamAssignmentRevoke)) return Failure();
        var reason = ExamTaskValidator.ValidateReason(input.Reason);
        if (!reason.IsSuccess) return reason;
        if (input.InProgressPolicy is not (RevocationScope.KeepScore or RevocationScope.VoidScore or RevocationScope.ForceSubmit or RevocationScope.VoidAttempt))
            return Result.Fail("答卷处理策略无效", "VALIDATION_ERROR");
        if (!await CanAccessAssignmentAsync(assignmentId, Permissions.ExamAssignmentRevoke)) return Result.Fail("安排不存在", "NOT_FOUND");
        var taskId = await uow.Db.ExamAssignments.AsNoTracking().Where(x => x.Id == assignmentId).Select(x => (long?)x.TaskId).SingleOrDefaultAsync();
        if (!taskId.HasValue) return Result.Fail("安排不存在", "NOT_FOUND");
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) { outcome = Result.Ok(); return; }
                await ExamLockService.LockTaskAsync(db, taskId.Value);
                await ExamLockService.LockAssignmentAsync(db, assignmentId);
                var assignment = await assignments.GetTrackedAsync(db, assignmentId);
                if (assignment is null) { outcome = Result.Fail("安排不存在", "NOT_FOUND"); return; }
                if (assignment.RowVersion != input.RowVersion) { outcome = Concurrency(); return; }
                if (assignment.Status == AssignmentStatus.Revoked) { outcome = Result.Fail("安排已撤回", "INVALID_STATE_TRANSITION"); return; }
                var task = await tasks.GetTrackedAsync(db, assignment.TaskId) ?? throw new InvalidOperationException("任务不存在");
                await RevokeAssignmentInternalAsync(db, assignment, task, input.Reason.Trim(), input.InProgressPolicy, api, false);
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { assignmentId }), "exam_assignment.revoke", "ExamAssignment", assignmentId,
                    after: new { assignment.Status, input.InProgressPolicy }, reason: input.Reason.Trim());
                await db.SaveChangesAsync(); outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (DbUpdateConcurrencyException) { return Concurrency(); }
    }

    public async Task<Result> GrantRetakeAsync(long assignmentId, GrantRetakeInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamAssignmentGrantRetake)) return Failure();
        if (input.ExtraAttempts is < 1 or > 100) return Result.Fail("追加次数需为 1–100", "VALIDATION_ERROR");
        if (string.IsNullOrWhiteSpace(input.Reason)) return Result.Fail("请填写加次原因", "REASON_REQUIRED");
        if (input.ExpiresAt <= clock.UtcNow) return Result.Fail("加次有效期必须晚于当前时间", "VALIDATION_ERROR");
        if (!await CanAccessAssignmentAsync(assignmentId, Permissions.ExamAssignmentGrantRetake)) return Result.Fail("安排不存在", "NOT_FOUND");
        var currentAssignment = await uow.Db.ExamAssignments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assignmentId);
        if (currentAssignment is null) return Result.Fail("安排不存在", "NOT_FOUND");
        if (currentAssignment.Status is "Revoked" or "Expired" or "Closed") return Result.Fail("终态安排不能追加次数", "INVALID_STATE_TRANSITION");
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) return;
                await ExamLockService.LockAssignmentAsync(db, assignmentId);
                var assignment = await assignments.GetTrackedAsync(db, assignmentId);
                if (assignment is null) throw new InvalidOperationException("NOT_FOUND");
                if (assignment.RowVersion != input.RowVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
                var grant = new RetakeGrant { AssignmentId = assignmentId, ExtraAttempts = input.ExtraAttempts, Reason = input.Reason.Trim(),
                    GrantedBy = ActorId(), ExpiresAt = input.ExpiresAt, IdempotencyKey = $"retake:{RequiredKey(api)}", CreatedAt = clock.UtcNow };
                db.RetakeGrants.Add(grant); assignment.RowVersion++;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { assignmentId, grantId = grant.Id }), "exam_assignment.grant_retake", "ExamAssignment", assignmentId,
                    after: new { input.ExtraAttempts, input.ExpiresAt, input.Reason });
                await db.SaveChangesAsync();
                outbox.Add(db, "exam_assignment.retake_granted", "ExamAssignment", assignmentId, new { assignmentId, grantId = grant.Id, input.ExtraAttempts }, $"retake-grant:{grant.Id}", clock.UtcNow);
                await db.SaveChangesAsync();
            });
            return Result.Ok();
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (InvalidOperationException ex) { return Result.Fail(MessageFor(ex.Message), ex.Message); }
        catch (DbUpdateConcurrencyException) { return Concurrency(); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return Result.Fail("加次请求已处理", "IDEMPOTENCY_REPLAY"); }
    }

    public async Task<Result> RevokeTaskInternalAsync(ExamDbContext db, ExamTask task, string reason, string policy, ApiAuditMetadata? api)
    {
        var items = await assignments.GetTaskAssignmentsAsync(db, task.Id, true);
        var affected = 0;
        foreach (var assignment in items.OrderBy(x => x.Id))
        {
            if (assignment.Status is "Revoked" or "Closed" or "Expired") continue;
            await ExamLockService.LockAssignmentAsync(db, assignment.Id);
            await RevokeAssignmentInternalAsync(db, assignment, task, reason, policy, api, false);
            affected++;
        }
        return Result.Ok();
    }

    private async Task<bool> CanAccessAssignmentAsync(long assignmentId, string permission)
    {
        var taskId = await uow.Db.ExamAssignments.AsNoTracking().Where(x => x.Id == assignmentId)
            .Select(x => x.TaskId).SingleOrDefaultAsync();
        if (taskId == 0) return false;
        var task = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        return task is not null && await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, permission);
    }

    private async Task ProcessBatchAsync(ExamDbContext db, AssignmentBatch batch, IReadOnlyList<AssignmentBatchRow> rows,
        ExamTask task, AssignmentTargetInput target, DateTime now)
    {
        foreach (var row in rows.Where(x => x.Status is "Pending" or "Failed").OrderBy(x => x.RowNo))
        {
            var startStatus = row.Status;
            await FinishRowAsync(db, row, "Processing", null, "处理中", now);
            if (row.UserId is null) { await FinishRowAsync(db, row, "Failed", null, "用户不存在", now); continue; }
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == row.UserId.Value);
            if (user is null || user.Status != "Active") { await FinishRowAsync(db, row, "Failed", null, "用户不存在或已停用", now); continue; }
            var active = await assignments.FindActiveAsync(db, task.Id, user.Id);
            if (active is not null) { await FinishRowAsync(db, row, startStatus == "Pending" ? "Skipped" : "Failed", null, "已有有效安排", now); continue; }
            ExamAssignment? old = null;
            if (target.ReissueRevoked)
                old = await db.ExamAssignments.AsNoTracking().Where(x => x.TaskId == task.Id && x.UserId == user.Id && x.Status == "Revoked")
                    .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
            var membership = await (from item in db.UserDepartments.AsNoTracking()
                                    join department in db.Departments.AsNoTracking() on item.DepartmentId equals department.Id
                                    where item.UserId == user.Id && department.Path.StartsWith(
                                        db.Departments.AsNoTracking().Where(d => d.Id == task.DepartmentId).Select(d => d.Path).First())
                                    orderby item.IsPrimary descending, item.DepartmentId
                                    select new { item.DepartmentId, department.Path }).FirstOrDefaultAsync();
            if (membership is null) { await FinishRowAsync(db, row, "Failed", null, "用户不在任务部门范围", now); continue; }
            var assignment = new ExamAssignment
            {
                BatchId = batch.Id, TaskId = task.Id, UserId = user.Id, DepartmentIdSnapshot = membership.DepartmentId,
                Status = AssignmentStatus.Pending, AvailableFrom = task.AvailableFrom, AvailableTo = task.AvailableTo,
                InheritedAttemptsUsed = old is not null && target.InheritAttempts ? old.AttemptsUsed + old.InheritedAttemptsUsed : 0,
                ReissuedFromId = old?.Id, CreatedAt = now, RowVersion = 1
            };
            await assignments.AddAssignmentAsync(db, assignment); await db.SaveChangesAsync();
            row.Status = "Success"; row.AssignmentId = assignment.Id; row.ErrorJson = "[]"; row.RowVersion++; row.UpdatedAt = now;
            outbox.Add(db, "exam_assignment.created", "ExamAssignment", assignment.Id, new { assignmentId = assignment.Id, taskId = task.Id, userId = user.Id }, $"assignment-created:{assignment.Id}", now);
            await db.SaveChangesAsync();
        }
        var successCount = rows.Count(x => x.Status == "Success");
        var failedCount = rows.Count(x => x.Status == "Failed");
        var skippedCount = rows.Count(x => x.Status == "Skipped");
        batch.Status = failedCount == 0 ? "Completed" : successCount == 0 ? "Failed" : "PartiallyFailed";
        batch.SuccessCount = successCount; batch.FailedCount = failedCount; batch.SkippedCount = skippedCount; batch.TotalCount = rows.Count;
        batch.FinishedAt = now; batch.RowVersion++; await db.SaveChangesAsync();
    }

    private async Task FinishRowAsync(ExamDbContext db, AssignmentBatchRow row, string status, long? assignmentId, string message, DateTime now)
    {
        row.Status = status; row.AssignmentId = assignmentId; row.ErrorJson = Serialize(new[] { new { code = status == "Failed" ? "ASSIGNMENT_FAILED" : "ASSIGNMENT_SKIPPED", message } });
        row.UpdatedAt = now; row.RowVersion++; await db.SaveChangesAsync();
    }

    private async Task RevokeAssignmentInternalAsync(ExamDbContext db, ExamAssignment assignment, ExamTask task,
        string reason, string policy, ApiAuditMetadata? api, bool writeAudit)
    {
        var now = clock.UtcNow;
        var inProgress = await db.ExamAttempts.FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id && x.Status == AttemptStatus.InProgress);
        if (inProgress is not null)
        {
            await ExamLockService.LockAttemptAsync(db, inProgress.Id);
            await db.Entry(inProgress).ReloadAsync();
        }
        if (inProgress is not null && inProgress.Status == AttemptStatus.InProgress)
        {
            if (policy == RevocationScope.ForceSubmit) await attemptService.ForceSubmitInternalAsync(db, inProgress, assignment, task, ActorId(), reason, now);
            else if (policy == RevocationScope.VoidScore) await attemptService.ForceSubmitInternalAsync(db, inProgress, assignment, task, ActorId(), reason, now, zeroScore: true);
            else if (policy is RevocationScope.VoidAttempt or RevocationScope.KeepScore)
            {
                // KeepScore 保留已提交/已判分的历史成绩；未交卷的进行中答卷必须终止作废，避免在 Revoked 安排下残留可继续作答或触发超时扫描异常的 InProgress 答卷。
                inProgress.Status = AttemptStatus.Voided; inProgress.VoidedAt = now;
                inProgress.VoidReason = policy == RevocationScope.KeepScore ? $"{reason}（保留已有成绩，终止未交卷作答）" : reason;
                inProgress.RowVersion++;
                assignmentsEvent(db, inProgress.Id, ActorId(), "Voided", new { reason, policy }, $"attempt-void:{inProgress.Id}:{inProgress.RowVersion}", now);
                await db.SaveChangesAsync();
            }
        }
        assignment.Status = AssignmentStatus.Revoked; assignment.RevokedAt = now; assignment.RevokeReason = reason; assignment.RowVersion++;
        await db.SaveChangesAsync();
        db.RevocationRecords.Add(new RevocationRecord { TargetType = "ExamAssignment", TargetId = assignment.Id, Scope = policy,
            Reason = reason, ImpactJson = Serialize(new { inProgressAttemptId = inProgress?.Id }), ActorId = ActorId(), ActorRole = "User",
            IdempotencyKey = $"assignment-revoke:{RequiredKey(api)}:{assignment.Id}", CreatedAt = now });
        var batchRow = await db.AssignmentBatchRows.FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id && x.Status == "Success");
        if (batchRow is not null && await db.AssignmentBatches.AsNoTracking().AnyAsync(x => x.Id == batchRow.BatchId && x.Status != "Completed" && x.Status != "Revoked"))
        { batchRow.Status = "Revoked"; batchRow.RowVersion++; batchRow.UpdatedAt = now; }
        await db.SaveChangesAsync();
        outbox.Add(db, "exam_assignment.revoked", "ExamAssignment", assignment.Id, new { assignmentId = assignment.Id, taskId = assignment.TaskId, policy }, $"assignment-revoked:{assignment.Id}:{assignment.RowVersion}", now);
        await db.SaveChangesAsync();
        if (writeAudit) await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { assignmentId = assignment.Id }), "exam_assignment.revoke", "ExamAssignment", assignment.Id,
            after: new { assignment.Status, policy }, reason: reason);
    }

    private void assignmentsEvent(ExamDbContext db, long attemptId, long actorId, string type, object payload, string key, DateTime now)
        => db.AttemptEvents.Add(new AttemptEvent { AttemptId = attemptId, ActorId = actorId, EventType = type, PayloadJson = Serialize(payload), EventKey = key, OccurredAt = now });

    private void Require(string permission)
    { if (!currentUser.IsAuthenticated || !currentUser.HasPermission(permission)) throw new UnauthorizedAccessException("权限不足"); }
    private bool Can(string permission) => currentUser.IsAuthenticated && currentUser.HasPermission(permission);
    private long ActorId() => currentUser.Id ?? throw new InvalidOperationException("认证用户缺少 ID");
    private static string RequiredKey(ApiAuditMetadata? api) => api?.IdempotencyKey ?? throw new InvalidOperationException("操作必须提供幂等键");
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static Result Failure() => Result.Fail("权限不足", "PERMISSION_DENIED");
    private static Result<T> Failure<T>() => Result<T>.Fail("权限不足", "PERMISSION_DENIED");
    private static Result OutOfScope() => Result.Fail("无权操作该资源", "OUT_OF_SCOPE");
    private static Result<T> OutOfScope<T>() => Result<T>.Fail("无权操作该资源", "OUT_OF_SCOPE");
    private static Result Conflict() => Result.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private static Result<T> Conflict<T>() => Result<T>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private static Result Concurrency() => Result.Fail("数据已被其他操作修改，请刷新后重试", "CONCURRENCY_CONFLICT");
    private static Result<T> Concurrency<T>() => Result<T>.Fail("数据已被其他操作修改，请刷新后重试", "CONCURRENCY_CONFLICT");
    private static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private static string MessageFor(string code) => code switch
    {
        "NOT_FOUND" => "资源不存在", "CONCURRENCY_CONFLICT" => "数据已被其他操作修改，请刷新后重试",
        "EXAM_TASK_NOT_PUBLISHED" => "只能操作已发布任务", "NO_TARGETS" => "没有匹配的下发对象",
        "BATCH_NOT_RETRYABLE" => "当前批次不能重试", _ => "操作失败，请刷新后重试"
    };
}
