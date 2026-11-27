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

public sealed class ExamTaskService(
    IUnitOfWork uow,
    ExamTaskRepository tasks,
    AssignmentService assignments,
    DataScopeService scope,
    AuditRepository audit,
    OutboxRepository outbox,
    ExamIdempotencyService idempotency,
    ResultReleaseService results,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<PagedResult<ExamTaskRow>> GetPagedAsync(ExamTaskQuery query)
    {
        await currentUser.LoadAsync();
        Require(Permissions.ExamTaskRead);
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.ExamTaskRead);
        var assigned = await scope.GetAssignedTaskIdsAsync(Permissions.ExamTaskRead);
        return await tasks.GetPagedAsync(uow.Db, allowed, query with
        {
            Keyword = string.IsNullOrWhiteSpace(query.Keyword) ? null : query.Keyword.Trim(),
            PageIndex = Math.Max(1, query.PageIndex), PageSize = Math.Clamp(query.PageSize, 1, 100)
        }, assigned);
    }

    public async Task<ExamTaskDetail?> GetDetailAsync(long taskId)
    {
        await currentUser.LoadAsync();
        Require(Permissions.ExamTaskRead);
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.ExamTaskRead);
        var assigned = await scope.GetAssignedTaskIdsAsync(Permissions.ExamTaskRead);
        return await tasks.GetDetailAsync(uow.Db, taskId, allowed, assigned);
    }

    public async Task<IReadOnlyList<ExamTaskStaffRow>> GetStaffAsync(long taskId)
    {
        if (await GetDetailAsync(taskId) is null) return [];
        return await tasks.GetStaffAsync(uow.Db, taskId);
    }

    public async Task<Result<ExamTaskMutationResult>> CreateAsync(CreateExamTaskInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamTaskCreate)) return Failure<ExamTaskMutationResult>();
        var validation = ExamTaskValidator.Validate(input);
        if (!validation.IsSuccess) return Result<ExamTaskMutationResult>.Fail(validation.Error!, validation.Code);
        if (!(await scope.EnsureDepartmentAccessAsync(input.DepartmentId, Permissions.ExamTaskCreate)).IsSuccess)
            return Result<ExamTaskMutationResult>.Fail("无权在该部门创建考试任务", "OUT_OF_SCOPE");
        var paperFailure = await ValidatePaperAsync(input.PaperVersionId, input.DepartmentId, false);
        if (paperFailure is not null) return Result<ExamTaskMutationResult>.Fail(paperFailure.Error!, paperFailure.Code);
        var now = clock.UtcNow;
        ExamTaskMutationResult? replay = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                replay = await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api);
                if (replay is not null) return;
                var task = new ExamTask
                {
                    DepartmentId = input.DepartmentId, Code = input.Code.Trim(), Title = input.Title.Trim(),
                    Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
                    ExamDate = input.ExamDate, PaperVersionId = input.PaperVersionId, Status = ExamTaskStatus.Draft,
                    AvailableFrom = DateTime.SpecifyKind(input.AvailableFrom, DateTimeKind.Unspecified),
                    AvailableTo = DateTime.SpecifyKind(input.AvailableTo, DateTimeKind.Unspecified),
                    DurationMinutes = input.DurationMinutes, AttemptLimit = input.AttemptLimit,
                    ScorePolicy = input.ScorePolicy, RetakeApproval = input.RetakeApproval,
                    RetakeCooldownMinutes = input.RetakeCooldownMinutes, PassScore = input.PassScore,
                    ResultReleasePolicy = input.ResultReleasePolicy, ResultStatus = "Unpublished",
                    LateScorePolicy = input.LateScorePolicy, CreatedBy = ActorId(), CreatedAt = now, RowVersion = 1
                };
                db.ExamTasks.Add(task);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { taskId = task.Id }),
                    "exam_task.create", "ExamTask", task.Id, after: new { task.Code, task.Title, task.DepartmentId });
                await db.SaveChangesAsync();
                replay = new(task.Id);
            });
            return Result<ExamTaskMutationResult>.Ok(replay!);
        }
        catch (ExamIdempotencyConflictException) { return Conflict<ExamTaskMutationResult>(); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return Result<ExamTaskMutationResult>.Fail("任务编码已存在", "DUPLICATE_EXAM_TASK_CODE"); }
    }

    public async Task<Result> UpdateDraftAsync(long taskId, UpdateExamTaskInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamTaskUpdate)) return Failure();
        var validation = ExamTaskValidator.Validate(input);
        if (!validation.IsSuccess) return validation;
        var existing = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (existing is null) return Result.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(existing.Id, existing.DepartmentId, Permissions.ExamTaskUpdate)) return OutOfScope();
        var paperFailure = await ValidatePaperAsync(input.PaperVersionId, existing.DepartmentId, false);
        if (paperFailure is not null) return paperFailure;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) { outcome = Result.Ok(); return; }
                await ExamLockService.LockTaskAsync(db, taskId);
                var task = await tasks.GetTrackedAsync(db, taskId);
                if (task is null) { outcome = Result.Fail("考试任务不存在", "NOT_FOUND"); return; }
                if (task.RowVersion != input.RowVersion) { outcome = Concurrency(); return; }
                if (task.Status != ExamTaskStatus.Draft) { outcome = Result.Fail("只能修改草稿任务", "EXAM_TASK_NOT_DRAFT"); return; }
                var before = new { task.Title, task.PaperVersionId, task.AvailableFrom, task.AvailableTo, task.DurationMinutes, task.AttemptLimit };
                task.Title = input.Title.Trim(); task.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
                task.ExamDate = input.ExamDate; task.PaperVersionId = input.PaperVersionId;
                task.AvailableFrom = DateTime.SpecifyKind(input.AvailableFrom, DateTimeKind.Unspecified);
                task.AvailableTo = DateTime.SpecifyKind(input.AvailableTo, DateTimeKind.Unspecified);
                task.DurationMinutes = input.DurationMinutes; task.AttemptLimit = input.AttemptLimit;
                task.ScorePolicy = input.ScorePolicy; task.RetakeApproval = input.RetakeApproval;
                task.RetakeCooldownMinutes = input.RetakeCooldownMinutes; task.PassScore = input.PassScore;
                task.ResultReleasePolicy = input.ResultReleasePolicy; task.LateScorePolicy = input.LateScorePolicy;
                task.UpdatedAt = clock.UtcNow; task.RowVersion++;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { taskId }), "exam_task.update", "ExamTask", taskId, before,
                    new { task.Title, task.PaperVersionId, task.AvailableFrom, task.AvailableTo, task.DurationMinutes, task.AttemptLimit });
                await db.SaveChangesAsync(); outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (DbUpdateConcurrencyException) { return Concurrency(); }
    }

    public async Task<Result> PublishAsync(long taskId, ExamTaskActionInput input, ApiAuditMetadata? api = null)
        => await TransitionAsync(taskId, input, Permissions.ExamTaskPublish, ExamTaskStatus.Draft, "publish", api);

    public async Task<Result> CloseAsync(long taskId, ExamTaskActionInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamTaskClose)) return Failure();
        var existing = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (existing is null) return Result.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(existing.Id, existing.DepartmentId, Permissions.ExamTaskClose)) return OutOfScope();
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) { outcome = Result.Ok(); return; }
                await ExamLockService.LockTaskAsync(db, taskId);
                var task = await tasks.GetTrackedAsync(db, taskId);
                if (task is null) { outcome = Result.Fail("考试任务不存在", "NOT_FOUND"); return; }
                if (task.RowVersion != input.RowVersion) { outcome = Concurrency(); return; }
                if (task.Status != ExamTaskStatus.Published) { outcome = Result.Fail("只能关闭已发布任务", "EXAM_TASK_NOT_PUBLISHED"); return; }
                task.Status = ExamTaskStatus.Closed; task.ClosedAt = clock.UtcNow; task.UpdatedAt = clock.UtcNow; task.RowVersion++;
                var pending = await db.ExamAssignments.Where(x => x.TaskId == taskId && (x.Status == AssignmentStatus.Pending || x.Status == AssignmentStatus.Available)).ToListAsync();
                foreach (var assignment in pending) { assignment.Status = AssignmentStatus.Expired; assignment.RowVersion++; }
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { taskId }), "exam_task.close", "ExamTask", taskId,
                    after: new { task.Status, EndedAssignmentCount = pending.Count });
                await db.SaveChangesAsync();
                outbox.Add(db, "exam_task.closed", "ExamTask", taskId, new { taskId, endedAssignmentCount = pending.Count }, $"task-close:{taskId}:{task.RowVersion}", clock.UtcNow);
                await db.SaveChangesAsync(); outcome = Result.Ok();
            });
            if (outcome?.IsSuccess == true) await results.TryAutoPublishAsync(taskId);
            return outcome!;
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (DbUpdateConcurrencyException) { return Concurrency(); }
    }

    public async Task<Result> ReopenAsync(long taskId, ExamTaskActionInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamTaskPublish)) return Failure();
        var existing = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (existing is null) return Result.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(existing.Id, existing.DepartmentId, Permissions.ExamTaskPublish)) return OutOfScope();
        if (existing.AvailableTo <= TimeFormat.ToLocal(clock.UtcNow)) return Result.Fail("考试时间窗已结束，不能重开", "EXAM_WINDOW_INVALID");
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) { outcome = Result.Ok(); return; }
                await ExamLockService.LockTaskAsync(db, taskId);
                var task = await tasks.GetTrackedAsync(db, taskId);
                if (task is null) { outcome = Result.Fail("考试任务不存在", "NOT_FOUND"); return; }
                if (task.RowVersion != input.RowVersion) { outcome = Concurrency(); return; }
                if (task.Status != ExamTaskStatus.Closed) { outcome = Result.Fail("只能重开已关闭任务", "EXAM_TASK_NOT_CLOSED"); return; }
                if (task.AvailableTo <= TimeFormat.ToLocal(clock.UtcNow)) { outcome = Result.Fail("考试时间窗已结束，不能重开", "EXAM_WINDOW_INVALID"); return; }
                task.Status = ExamTaskStatus.Published; task.ClosedAt = null; task.UpdatedAt = clock.UtcNow; task.RowVersion++;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { taskId }), "exam_task.reopen", "ExamTask", taskId,
                    after: new { task.Status });
                await db.SaveChangesAsync();
                outbox.Add(db, "exam_task.reopened", "ExamTask", taskId, new { taskId }, $"task-reopen:{taskId}:{task.RowVersion}", clock.UtcNow);
                await db.SaveChangesAsync(); outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (DbUpdateConcurrencyException) { return Concurrency(); }
    }

    public async Task<Result> ArchiveAsync(long taskId, ExamTaskActionInput input, ApiAuditMetadata? api = null)
        => await TransitionAsync(taskId, input, Permissions.ExamTaskUpdate, null, "archive", api);

    public async Task<Result> RevokeAsync(long taskId, RevokeExamTaskInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamTaskRevoke)) return Failure();
        var reason = ExamTaskValidator.ValidateReason(input.Reason);
        if (!reason.IsSuccess) return reason;
        if (input.InProgressPolicy is not (RevocationScope.KeepScore or RevocationScope.VoidScore or RevocationScope.ForceSubmit or RevocationScope.VoidAttempt))
            return Result.Fail("进行中答卷处理策略无效", "VALIDATION_ERROR");
        var existing = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (existing is null) return Result.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(existing.Id, existing.DepartmentId, Permissions.ExamTaskRevoke)) return OutOfScope();
        if (!string.Equals(input.ConfirmationTitle?.Trim(), existing.Title.Trim(), StringComparison.Ordinal))
            return Result.Fail("请输入正确的任务名称进行确认", "CONFIRMATION_REQUIRED");
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) { outcome = Result.Ok(); return; }
                await ExamLockService.LockTaskAsync(db, taskId);
                var task = await tasks.GetTrackedAsync(db, taskId);
                if (task is null) { outcome = Result.Fail("考试任务不存在", "NOT_FOUND"); return; }
                if (task.RowVersion != input.RowVersion) { outcome = Concurrency(); return; }
                if (task.Status != ExamTaskStatus.Published) { outcome = Result.Fail("只能撤回已发布任务", "EXAM_TASK_NOT_PUBLISHED"); return; }
                var revocation = await assignments.RevokeTaskInternalAsync(db, task, input.Reason.Trim(), input.InProgressPolicy, api);
                if (!revocation.IsSuccess) { outcome = revocation; return; }
                task.Status = ExamTaskStatus.Revoked; task.RevokedAt = clock.UtcNow; task.UpdatedAt = clock.UtcNow; task.RowVersion++;
                db.RevocationRecords.Add(new RevocationRecord
                {
                    TargetType = "ExamTask", TargetId = task.Id, Scope = input.InProgressPolicy, Reason = input.Reason.Trim(),
                    ImpactJson = "{}", ActorId = ActorId(), ActorRole = "User", IdempotencyKey = RequiredKey(api), CreatedAt = clock.UtcNow
                });
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { taskId }), "exam_task.revoke", "ExamTask", task.Id,
                    after: new { task.Status, input.InProgressPolicy }, reason: input.Reason.Trim());
                await db.SaveChangesAsync();
                outbox.Add(db, "exam_task.revoked", "ExamTask", task.Id, new { taskId, input.InProgressPolicy }, $"task-revoke:{task.Id}:{task.RowVersion}", clock.UtcNow);
                await db.SaveChangesAsync(); outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (DbUpdateConcurrencyException) { return Concurrency(); }
    }

    public async Task<Result> AddStaffAsync(long taskId, ExamTaskStaffInput input, ApiAuditMetadata? api = null)
        => await ChangeStaffAsync(taskId, input, true, api);

    public async Task<Result> RemoveStaffAsync(long taskId, long staffId, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamTaskUpdate)) return Failure();
        var task = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (task is null) return Result.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, Permissions.ExamTaskUpdate)) return OutOfScope();
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) return;
                await tasks.RemoveStaffAsync(db, taskId, staffId); await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { taskId, staffId }), "exam_task.staff_remove", "ExamTask", taskId);
                await db.SaveChangesAsync();
            });
            return Result.Ok();
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
    }

    private async Task<Result> ChangeStaffAsync(long taskId, ExamTaskStaffInput input, bool add, ApiAuditMetadata? api)
    {
        await currentUser.LoadAsync();
        if (!Can(Permissions.ExamTaskUpdate)) return Failure();
        if (input.StaffRole is not ("ExamManager" or "Grader" or "Invigilator")) return Result.Fail("考务角色无效", "VALIDATION_ERROR");
        var task = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (task is null) return Result.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, Permissions.ExamTaskUpdate)) return OutOfScope();
        var user = await uow.Db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.UserId);
        if (user is null || user.Status != "Active") return Result.Fail("只能添加启用用户", "USER_DISABLED");
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) return;
                await tasks.AddStaffAsync(db, new ExamTaskStaff { TaskId = taskId, UserId = input.UserId, StaffRole = input.StaffRole, AssignedBy = ActorId(), CreatedAt = clock.UtcNow });
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { taskId, userId = input.UserId }), "exam_task.staff_add", "ExamTask", taskId,
                    after: new { input.UserId, input.StaffRole });
                await db.SaveChangesAsync();
            });
            return Result.Ok();
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return Result.Fail("该用户已具有相同考务角色", "DUPLICATE_TASK_STAFF"); }
    }

    private async Task<Result> TransitionAsync(long taskId, ExamTaskActionInput input, string permission, string? requiredStatus, string action, ApiAuditMetadata? api)
    {
        await currentUser.LoadAsync();
        if (!Can(permission)) return Failure();
        var existing = await tasks.GetNoTrackingAsync(uow.Db, taskId);
        if (existing is null) return Result.Fail("考试任务不存在", "NOT_FOUND");
        if (!await scope.CanAccessTaskAsync(existing.Id, existing.DepartmentId, permission)) return OutOfScope();
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) { outcome = Result.Ok(); return; }
                await ExamLockService.LockTaskAsync(db, taskId);
                var task = await tasks.GetTrackedAsync(db, taskId);
                if (task is null) { outcome = Result.Fail("考试任务不存在", "NOT_FOUND"); return; }
                if (task.RowVersion != input.RowVersion) { outcome = Concurrency(); return; }
                if (action == "publish")
                {
                    if (task.Status != ExamTaskStatus.Draft) { outcome = Result.Fail("只能发布草稿任务", "EXAM_TASK_NOT_DRAFT"); return; }
                    if (task.PaperVersionId is null) { outcome = Result.Fail("请先选择已发布试卷", "PAPER_VERSION_NOT_PUBLISHED"); return; }
                    var paperFailure = await ValidatePaperAsync(task.PaperVersionId, task.DepartmentId, true);
                    if (paperFailure is not null) { outcome = paperFailure; return; }
                    var version = await db.PaperVersions.AsNoTracking().SingleAsync(x => x.Id == task.PaperVersionId.Value);
                    if (task.PassScore > version.TotalScore) { outcome = Result.Fail("及格分不能超过试卷总分", "VALIDATION_ERROR"); return; }
                    if (task.AvailableTo <= TimeFormat.ToLocal(clock.UtcNow)) { outcome = Result.Fail("考试时间窗已结束", "EXAM_WINDOW_INVALID"); return; }
                    task.Status = ExamTaskStatus.Published; task.PublishedAt = clock.UtcNow;
                }
                else
                {
                    if (task.Status is not (ExamTaskStatus.Draft or ExamTaskStatus.Closed or ExamTaskStatus.Revoked)) { outcome = Result.Fail("当前任务不能归档", "INVALID_STATE_TRANSITION"); return; }
                    if (await tasks.HasInProgressAttemptsAsync(db, taskId)) { outcome = Result.Fail("仍有进行中答卷，不能归档", "ATTEMPT_STILL_IN_PROGRESS"); return; }
                    if (task.Status == ExamTaskStatus.Draft && await tasks.HasAssignmentsAsync(db, taskId)) { outcome = Result.Fail("已有下发的任务不能作为草稿归档", "EXAM_TASK_HAS_ASSIGNMENTS"); return; }
                    task.Status = ExamTaskStatus.Archived; task.ArchivedAt = clock.UtcNow;
                }
                task.UpdatedAt = clock.UtcNow; task.RowVersion++;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { taskId }), $"exam_task.{action}", "ExamTask", task.Id,
                    after: new { task.Status });
                await db.SaveChangesAsync(); outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (DbUpdateConcurrencyException) { return Concurrency(); }
    }

    private async Task<Result?> ValidatePaperAsync(long? versionId, long departmentId, bool published)
    {
        if (versionId is null) return published ? Result.Fail("请先选择已发布试卷", "PAPER_VERSION_NOT_PUBLISHED") : null;
        var paper = await (from version in uow.Db.PaperVersions.AsNoTracking()
                           join item in uow.Db.Papers.AsNoTracking() on version.PaperId equals item.Id
                           where version.Id == versionId.Value
                           select new { version.Status, item.DepartmentId }).FirstOrDefaultAsync();
        if (paper is null || paper.DepartmentId != departmentId || (published && paper.Status != "Published"))
            return Result.Fail("试卷版本不存在、部门不匹配或尚未发布", "PAPER_VERSION_NOT_PUBLISHED");
        return null;
    }

    private void Require(string permission)
    {
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(permission)) throw new UnauthorizedAccessException("权限不足");
    }
    private bool Can(string permission) => currentUser.IsAuthenticated && currentUser.HasPermission(permission);
    private long ActorId() => currentUser.Id ?? throw new InvalidOperationException("认证用户缺少 ID");
    private static string RequiredKey(ApiAuditMetadata? api) => api?.IdempotencyKey ?? throw new InvalidOperationException("撤回操作必须提供幂等键");
    private static Result Failure() => Result.Fail("权限不足", "PERMISSION_DENIED");
    private static Result<T> Failure<T>() => Result<T>.Fail("权限不足", "PERMISSION_DENIED");
    private static Result OutOfScope() => Result.Fail("无权操作该考试任务", "OUT_OF_SCOPE");
    private static Result Conflict() => Result.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private static Result<T> Conflict<T>() => Result<T>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private static Result Concurrency() => Result.Fail("数据已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
    private static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
