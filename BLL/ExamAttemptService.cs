using System.Security.Cryptography;
using System.Text.Json;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using ExamOps.Model.Enums;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.BLL;

public sealed class ExamAttemptService(
    IUnitOfWork uow,
    AttemptRepository attempts,
    AssignmentRepository assignments,
    ExamTaskRepository tasks,
    AuditRepository audit,
    OutboxRepository outbox,
    ExamIdempotencyService idempotency,
    DataScopeService scope,
    ICurrentUser currentUser,
    IClock clock,
    ResultReleaseService results,
    ILogger<ExamAttemptService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    public async Task<PagedResult<MyExamRow>> GetMyExamsAsync(int pageIndex, int pageSize)
    {
        await currentUser.LoadAsync();
        var userId = RequireCandidate();
        return await attempts.GetMyPagedAsync(uow.Db, userId, Math.Max(1, pageIndex), Math.Clamp(pageSize, 1, 100));
    }

    public async Task<Result<StartAttemptResult>> StartAsync(long assignmentId, StartExamInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        var userId = RequireCandidate();
        var taskId = await uow.Db.ExamAssignments.AsNoTracking().Where(x => x.Id == assignmentId && x.UserId == userId)
            .Select(x => (long?)x.TaskId).FirstOrDefaultAsync();
        if (!taskId.HasValue) return Result<StartAttemptResult>.Fail("安排不存在", "NOT_FOUND");
        StartAttemptResult? replay = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                replay = await idempotency.FindReplayAsync<StartAttemptResult>(db, api);
                if (replay is not null) return;
                await ExamLockService.LockTaskAsync(db, taskId.Value);
                await ExamLockService.LockAssignmentAsync(db, assignmentId);
                var assignment = await assignments.GetTrackedAsync(db, assignmentId);
                if (assignment is null || assignment.UserId != userId) throw new InvalidOperationException("NOT_FOUND");
                if (assignment.RowVersion != input.AssignmentRowVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
                var task = await tasks.GetNoTrackingAsync(db, assignment.TaskId)
                    ?? throw new InvalidOperationException("NOT_FOUND");
                var now = clock.UtcNow;
                var nowLocal = TimeFormat.ToLocal(now);
                if (task.Status != ExamTaskStatus.Published) throw new InvalidOperationException("EXAM_TASK_NOT_PUBLISHED");
                if (task.ResultStatus == "Published") throw new InvalidOperationException("RESULT_ALREADY_PUBLISHED");
                if (assignment.Status == AssignmentStatus.Pending)
                {
                    // 开始考试与安排激活共用同一把锁，避免依赖后台轮询造成“窗口已开但不能开始”。
                    if (nowLocal < assignment.AvailableFrom) throw new InvalidOperationException("ASSIGNMENT_WINDOW_NOT_OPEN");
                    if (nowLocal >= assignment.AvailableTo) throw new InvalidOperationException("ASSIGNMENT_WINDOW_CLOSED");
                    assignment.Status = AssignmentStatus.Available;
                    assignment.RowVersion++;
                    await db.SaveChangesAsync();
                }
                if (assignment.Status == AssignmentStatus.InProgress)
                {
                    var existing = await db.ExamAttempts.AsNoTracking().FirstOrDefaultAsync(x => x.AssignmentId == assignmentId && x.Status == AttemptStatus.InProgress);
                    if (existing is not null) { replay = await BuildStartResultAsync(db, existing); return; }
                }
                var granted = await assignments.SumGrantedAttemptsAsync(db, assignmentId, now);
                var limit = (assignment.AttemptLimitOverride ?? task.AttemptLimit) + granted;
                var consumed = assignment.AttemptsUsed + assignment.InheritedAttemptsUsed;
                if (assignment.Status == AssignmentStatus.Closed && consumed >= limit)
                    throw new InvalidOperationException("ATTEMPT_LIMIT_REACHED");
                if (assignment.Status is not (AssignmentStatus.Available or AssignmentStatus.Graded or AssignmentStatus.Submitted))
                    throw new InvalidOperationException("INVALID_STATE_TRANSITION");
                if (nowLocal < assignment.AvailableFrom) throw new InvalidOperationException("ASSIGNMENT_WINDOW_NOT_OPEN");
                if (nowLocal >= assignment.AvailableTo) throw new InvalidOperationException("ASSIGNMENT_WINDOW_CLOSED");
                if (consumed >= limit) throw new InvalidOperationException("ATTEMPT_LIMIT_REACHED");
                if (consumed > 0)
                {
                    if (task.RetakeApproval == "Manager" && granted == 0) throw new InvalidOperationException("RETAKE_APPROVAL_REQUIRED");
                    var last = await db.ExamAttempts.AsNoTracking().Where(x => x.AssignmentId == assignmentId && x.Status != AttemptStatus.Voided && x.SubmittedAt != null)
                        .OrderByDescending(x => x.SubmittedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
                    if (last is not null && last.SubmittedAt!.Value.AddMinutes(task.RetakeCooldownMinutes) > now)
                        throw new InvalidOperationException("RETAKE_COOLDOWN_ACTIVE");
                }
                if (task.PaperVersionId is null) throw new InvalidOperationException("PAPER_VERSION_NOT_PUBLISHED");
                var (sections, pool) = await attempts.GetPaperSourcesAsync(db, task.PaperVersionId.Value);
                var seed = RandomNumberGenerator.GetBytes(32);
                var selection = ExamAlgorithms.SelectPaper(seed,
                    sections.Select(x => new ExamPaperSectionSource(x.Id, x.Title, x.SortOrder, x.SelectionMode, x.PickCount, x.ScorePerQuestion)).ToArray(),
                    pool.Select(x => new ExamPaperQuestionSource(x.PaperQuestionId, x.SectionId, x.QuestionVersionId, x.QuestionId, x.Score, x.IsPool)).ToArray());
                if (!selection.IsSuccess) throw new InvalidOperationException("PAPER_SELECTION_FAILED");
                var versionIds = selection.Questions.Select(x => x.QuestionVersionId).Distinct().ToArray();
                var sources = await attempts.GetAttemptQuestionSourcesAsync(db, versionIds);
                var options = await attempts.GetOptionsAsync(db, versionIds);
                var sourceMap = sources.ToDictionary(x => x.QuestionVersionId);
                var optionMap = options.GroupBy(x => x.QuestionVersionId).ToDictionary(x => x.Key, x => x.ToList());
                var candidateSections = selection.Questions.GroupBy(x => x.SectionId).OrderBy(x => sections.First(s => s.Id == x.Key).SortOrder)
                    .Select(group =>
                    {
                        var section = sections.First(x => x.Id == group.Key);
                        var questions = group.OrderBy(x => x.SortOrder).Select((item, index) =>
                        {
                            var source = sourceMap[item.QuestionVersionId];
                            return new AttemptQuestionDto(item.PaperQuestionId, item.QuestionVersionId, source.Type, source.Stem,
                                optionMap.GetValueOrDefault(item.QuestionVersionId, []).Select(o => new AttemptOptionDto(o.Label, o.Content)).ToArray(),
                                item.Score, index + 1, null);
                        }).ToArray();
                        return new AttemptSectionDto(section.Id, section.Title, questions);
                    }).ToArray();
                var snapshot = new StoredPaper(selection.SeedHex, selection.AlgorithmVersion, task.PaperVersionId.Value, candidateSections);
                var attemptNo = assignment.AttemptsUsed + assignment.InheritedAttemptsUsed + 1;
                var duration = assignment.DurationMinutesOverride ?? task.DurationMinutes;
                var attempt = new ExamAttempt
                {
                    AssignmentId = assignment.Id, AttemptNo = attemptNo, Status = AttemptStatus.InProgress,
                    StartKey = $"start:{assignment.Id}:{attemptNo}:{Guid.NewGuid():N}", PaperSnapshotJson = Serialize(snapshot),
                    StartedAt = now, DeadlineAt = now.AddMinutes(duration), MaxScore = selection.Questions.Sum(x => x.Score), RowVersion = 1
                };
                db.ExamAttempts.Add(attempt);
                assignment.Status = AssignmentStatus.InProgress; assignment.AttemptsUsed++; assignment.RowVersion++;
                await db.SaveChangesAsync();
                foreach (var question in candidateSections.SelectMany(x => x.Questions))
                    attempts.AddAnswer(db, new AttemptAnswer { AttemptId = attempt.Id, QuestionVersionId = question.QuestionVersionId,
                        AnswerJson = null, MaxScore = question.Score, GradingStatus = "Pending", CreatedAt = now, UpdatedAt = now, RowVersion = 1 });
                attempts.AddEvent(db, NewEvent(attempt.Id, userId, "Started", new { attempt.AttemptNo, attempt.StartKey }, attempt.StartKey));
                await db.SaveChangesAsync();
                replay = await BuildStartResultAsync(db, attempt);
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, replay), "exam_attempt.start", "ExamAttempt", attempt.Id,
                    after: new { attempt.AssignmentId, attempt.AttemptNo, attempt.StartedAt, attempt.DeadlineAt, AssignmentStatus = assignment.Status });
                await db.SaveChangesAsync();
                outbox.Add(db, "exam_attempt.started", "ExamAttempt", attempt.Id, new { attemptId = attempt.Id, assignmentId, userId }, $"attempt-start:{attempt.Id}", now);
                await db.SaveChangesAsync();
            });
            return Result<StartAttemptResult>.Ok(replay!);
        }
        catch (ExamIdempotencyConflictException) { return Result<StartAttemptResult>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT"); }
        catch (InvalidOperationException ex) { return Result<StartAttemptResult>.Fail(MessageFor(ex.Message), ex.Message); }
        catch (DbUpdateConcurrencyException) { return Result<StartAttemptResult>.Fail("数据已被其他操作修改，请刷新后重试", "CONCURRENCY_CONFLICT"); }
    }

    public async Task<AttemptDetail?> GetDetailAsync(long attemptId)
    {
        await currentUser.LoadAsync();
        var userId = RequireCandidate();
        var attempt = await attempts.GetNoTrackingAsync(uow.Db, attemptId);
        if (attempt is null) return null;
        var assignment = await uow.Db.ExamAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attempt.AssignmentId);
        if (assignment is null || assignment.UserId != userId) return null;
        var task = await uow.Db.ExamTasks.AsNoTracking().FirstAsync(x => x.Id == assignment.TaskId);
        var stored = Deserialize<StoredPaper>(attempt.PaperSnapshotJson);
        var paper = await BuildPaperAsync(uow.Db, stored, attempt.Id);
        var now = clock.UtcNow;
        var published = task.ResultStatus == "Published";
        return new AttemptDetail(attempt.Id, attempt.AssignmentId, task.Id, task.Title, attempt.AttemptNo, attempt.Status,
            attempt.RowVersion, attempt.SubmitKey, attempt.StartedAt, attempt.DeadlineAt, attempt.SubmittedAt,
            attempt.MaxScore, published ? attempt.AutoScore : null, published ? attempt.Score : null, attempt.IsLate, paper, now,
            attempt.Status == AttemptStatus.InProgress && task.Status != ExamTaskStatus.Revoked && assignment.Status != AssignmentStatus.Revoked && now <= attempt.DeadlineAt.Add(Grace),
            attempt.Status == AttemptStatus.InProgress && task.Status != ExamTaskStatus.Revoked && assignment.Status != AssignmentStatus.Revoked);
    }

    public async Task<Result> SaveAnswersAsync(long attemptId, SaveAnswersInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        var userId = RequireCandidate();
        if (input.Answers.Count > 500) return Result.Fail("单次保存题目过多", "VALIDATION_ERROR");
        var scopeInfo = await (from a in uow.Db.ExamAttempts.AsNoTracking()
                               join asn in uow.Db.ExamAssignments.AsNoTracking() on a.AssignmentId equals asn.Id
                               where a.Id == attemptId && asn.UserId == userId
                               select new { asn.TaskId, AssignmentId = asn.Id }).FirstOrDefaultAsync();
        if (scopeInfo is null) return Result.Fail("答卷不存在", "NOT_FOUND");
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) { outcome = Result.Ok(); return; }
                // 统一锁序：Task -> Assignment -> Attempt，避免与撤回/交卷并发死锁。
                await ExamLockService.LockTaskAsync(db, scopeInfo.TaskId);
                await ExamLockService.LockAssignmentAsync(db, scopeInfo.AssignmentId);
                await ExamLockService.LockAttemptAsync(db, attemptId);
                var attempt = await attempts.GetTrackedAsync(db, attemptId);
                if (attempt is null) { outcome = Result.Fail("答卷不存在", "NOT_FOUND"); return; }
                var assignment = await assignments.GetTrackedAsync(db, attempt.AssignmentId);
                if (assignment is null || assignment.UserId != userId) { outcome = Result.Fail("答卷不存在", "NOT_FOUND"); return; }
                if (attempt.RowVersion != input.RowVersion) { outcome = Concurrency(); return; }
                if (attempt.Status != AttemptStatus.InProgress) { outcome = Result.Fail("答卷已交卷，答案不可修改", "ANSWER_FROZEN"); return; }
                if (assignment.Status == AssignmentStatus.Revoked) { outcome = Result.Fail("考试安排已撤回，答案不可修改", "ASSIGNMENT_REVOKED"); return; }
                var task = await db.ExamTasks.AsNoTracking().FirstAsync(x => x.Id == assignment.TaskId);
                if (task.Status == ExamTaskStatus.Revoked) { outcome = Result.Fail("考试任务已撤回，答案不可修改", "EXAM_TASK_REVOKED"); return; }
                var now = clock.UtcNow;
                if (now > attempt.DeadlineAt.Add(Grace)) { outcome = Result.Fail("已超过允许保存时间", "ANSWER_FROZEN"); return; }
                var stored = Deserialize<StoredPaper>(attempt.PaperSnapshotJson);
                var snapshotQuestions = stored.Sections.SelectMany(x => x.Questions).ToDictionary(x => x.QuestionVersionId);
                if (input.Answers.Select(x => x.QuestionVersionId).Distinct().Count() != input.Answers.Count
                    || input.Answers.Any(x => !snapshotQuestions.ContainsKey(x.QuestionVersionId)))
                { outcome = Result.Fail("答案中包含不属于本卷的题目", "ANSWER_FORMAT_INVALID"); return; }
                var versionIds = snapshotQuestions.Keys.ToArray();
                var sources = (await attempts.GetAttemptQuestionSourcesAsync(db, versionIds)).ToDictionary(x => x.QuestionVersionId);
                var options = (await attempts.GetOptionsAsync(db, versionIds)).GroupBy(x => x.QuestionVersionId)
                    .ToDictionary(x => x.Key, x => x.Select(o => o.Label).ToHashSet(StringComparer.Ordinal));
                var answerMap = (await attempts.GetAnswersTrackedAsync(db, attemptId))
                    .ToDictionary(x => x.QuestionVersionId);
                foreach (var item in input.Answers)
                {
                    var source = sources[item.QuestionVersionId];
                    var validation = ExamAlgorithms.ValidateAnswer(source.Type, item.AnswerJson, options.GetValueOrDefault(item.QuestionVersionId, []));
                    if (!validation.IsSuccess) { outcome = Result.Fail(validation.Error!, "ANSWER_FORMAT_INVALID"); return; }
                    if (!answerMap.TryGetValue(item.QuestionVersionId, out var answer)) { outcome = Result.Fail("答卷题目记录缺失", "ANSWER_FORMAT_INVALID"); return; }
                    answer.AnswerJson = validation.Value; answer.UpdatedAt = now; answer.RowVersion++;
                }
                attempt.RowVersion++;
                attempts.AddEvent(db, NewEvent(attempt.Id, userId, "AnswersSaved", new { savedCount = input.Answers.Count }, $"answer-save:{attempt.Id}:{attempt.RowVersion}"));
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { attemptId }), "exam_attempt.save_answers", "ExamAttempt", attemptId,
                    after: new { savedCount = input.Answers.Count, attempt.RowVersion });
                await db.SaveChangesAsync(); outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (DbUpdateConcurrencyException) { return Concurrency(); }
    }

    public async Task<Result<AttemptSubmitResult>> SubmitAsync(long attemptId, SubmitAttemptInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        var userId = RequireCandidate();
        var scopeInfo = await (from a in uow.Db.ExamAttempts.AsNoTracking()
                               join asn in uow.Db.ExamAssignments.AsNoTracking() on a.AssignmentId equals asn.Id
                               where a.Id == attemptId && asn.UserId == userId
                               select new { asn.TaskId, AssignmentId = asn.Id }).FirstOrDefaultAsync();
        if (scopeInfo is null) return Result<AttemptSubmitResult>.Fail("答卷不存在", "NOT_FOUND");
        AttemptSubmitResult? replay = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                replay = await idempotency.FindReplayAsync<AttemptSubmitResult>(db, api);
                if (replay is not null) return;
                await ExamLockService.LockTaskAsync(db, scopeInfo.TaskId);
                await ExamLockService.LockAssignmentAsync(db, scopeInfo.AssignmentId);
                await ExamLockService.LockAttemptAsync(db, attemptId);
                var attempt = await attempts.GetTrackedAsync(db, attemptId);
                if (attempt is null) throw new InvalidOperationException("NOT_FOUND");
                var assignment = await assignments.GetTrackedAsync(db, attempt.AssignmentId);
                if (assignment is null || assignment.UserId != userId) throw new InvalidOperationException("NOT_FOUND");
                if (attempt.RowVersion != input.RowVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
                if (attempt.Status != AttemptStatus.InProgress) throw new InvalidOperationException("ATTEMPT_ALREADY_SUBMITTED");
                if (assignment.Status == AssignmentStatus.Revoked) throw new InvalidOperationException("ASSIGNMENT_REVOKED");
                var task = await db.ExamTasks.AsNoTracking().FirstAsync(x => x.Id == assignment.TaskId);
                if (task.Status == ExamTaskStatus.Revoked) throw new InvalidOperationException("EXAM_TASK_REVOKED");
                var result = await SubmitInternalAsync(db, attempt, assignment, task, false, userId, null, clock.UtcNow);
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, result), "exam_attempt.submit", "ExamAttempt", attempt.Id,
                    after: new { attempt.Status, attempt.SubmittedAt, attempt.IsLate });
                await db.SaveChangesAsync();
                replay = result with { Replayed = false };
            });
            if (replay is not null && !replay.Replayed)
            {
                var submittedTaskId = await uow.Db.ExamAssignments.AsNoTracking()
                    .Where(x => x.Id == uow.Db.ExamAttempts.Where(a => a.Id == attemptId).Select(a => a.AssignmentId).FirstOrDefault())
                    .Select(x => (long?)x.TaskId).FirstOrDefaultAsync();
                if (submittedTaskId.HasValue) await results.TryAutoPublishAsync(submittedTaskId.Value);
            }
            return Result<AttemptSubmitResult>.Ok(replay!);
        }
        catch (ExamIdempotencyConflictException) { return Result<AttemptSubmitResult>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT"); }
        catch (InvalidOperationException ex) { return Result<AttemptSubmitResult>.Fail(MessageFor(ex.Message), ex.Message); }
        catch (DbUpdateConcurrencyException) { return Result<AttemptSubmitResult>.Fail("数据已被其他操作修改，请刷新后重试", "CONCURRENCY_CONFLICT"); }
    }

    public async Task<AttemptResultDto?> GetResultAsync(long attemptId)
    {
        await currentUser.LoadAsync();
        var userId = RequireCandidate();
        var attempt = await attempts.GetNoTrackingAsync(uow.Db, attemptId);
        if (attempt is null) return null;
        var assignment = await uow.Db.ExamAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attempt.AssignmentId);
        if (assignment is null || assignment.UserId != userId) return null;
        var task = await uow.Db.ExamTasks.AsNoTracking().FirstAsync(x => x.Id == assignment.TaskId);
        var published = task.ResultStatus == "Published";
        var snapshot = published ? await results.GetPublishedItemAsync(assignment.Id, userId) : null;
        var finalAttempt = snapshot?.AttemptId is { } finalId
            ? await uow.Db.ExamAttempts.AsNoTracking().Where(x => x.Id == finalId).Select(x => (int?)x.AttemptNo).FirstOrDefaultAsync()
            : null;
        return new AttemptResultDto(attempt.Id, task.Id, task.Title, attempt.AttemptNo, attempt.Status, task.ResultStatus,
            snapshot?.FinalScore, snapshot?.MaxScore ?? attempt.MaxScore, published && snapshot is not null,
            attempt.SubmittedAt, attempt.GradedAt, snapshot?.AttemptId, finalAttempt,
            published && task.ResultReleaseId is { } releaseId
                ? await uow.Db.ResultReleases.AsNoTracking().Where(x => x.Id == releaseId).Select(x => (int?)x.ReleaseNo).FirstOrDefaultAsync()
                : null);
    }

    public async Task<Result> VoidAsync(long attemptId, VoidAttemptInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.ExamAttemptVoid)) return Failure();
        var reason = Validators.ExamTaskValidator.ValidateReason(input.Reason);
        if (!reason.IsSuccess) return reason;
        var attempt = await attempts.GetNoTrackingAsync(uow.Db, attemptId);
        if (attempt is null) return Result.Fail("答卷不存在", "NOT_FOUND");
        var assignment = await uow.Db.ExamAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attempt.AssignmentId);
        if (assignment is null) return Result.Fail("答卷不存在", "NOT_FOUND");
        var task = await uow.Db.ExamTasks.AsNoTracking().FirstAsync(x => x.Id == assignment.TaskId);
        if (!await CanAccessTaskAsync(task)) return OutOfScope();
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (await idempotency.FindReplayAsync<ExamTaskMutationResult>(db, api) is not null) { outcome = Result.Ok(); return; }
                await ExamLockService.LockTaskAsync(db, task.Id);
                await ExamLockService.LockAssignmentAsync(db, assignment.Id);
                await ExamLockService.LockAttemptAsync(db, attemptId);
                var tracked = await attempts.GetTrackedAsync(db, attemptId);
                var currentTask = await tasks.GetTrackedAsync(db, task.Id);
                if (tracked is null || currentTask is null) { outcome = Result.Fail("答卷不存在", "NOT_FOUND"); return; }
                if (currentTask.ResultStatus == "Published") { outcome = Result.Fail("成绩已发布，请先撤回成绩后再作废", "RESULT_ALREADY_PUBLISHED"); return; }
                if (tracked.RowVersion != input.RowVersion) { outcome = Concurrency(); return; }
                if (tracked.Status == AttemptStatus.Voided) { outcome = Result.Fail("答卷已作废", "INVALID_STATE_TRANSITION"); return; }
                var now = clock.UtcNow;
                tracked.Status = AttemptStatus.Voided; tracked.Score = 0; tracked.VoidedAt = now;
                tracked.VoidReason = input.Reason.Trim(); tracked.RowVersion++;
                var current = await assignments.GetTrackedAsync(db, tracked.AssignmentId) ?? throw new InvalidOperationException("安排不存在");
                var grantCount = await assignments.SumGrantedAttemptsAsync(db, current.Id, now);
                var limit = (current.AttemptLimitOverride ?? currentTask.AttemptLimit) + grantCount;
                if (current.AttemptsUsed + current.InheritedAttemptsUsed < limit && currentTask.Status == ExamTaskStatus.Published)
                    current.Status = AssignmentStatus.Available;
                else if (currentTask.Status != ExamTaskStatus.Published) current.Status = AssignmentStatus.Graded;
                else current.Status = AssignmentStatus.Closed;
                current.RowVersion++;
                await db.SaveChangesAsync();
                attempts.AddEvent(db, NewEvent(tracked.Id, currentUser.Id, "Voided", new { input.Reason }, $"attempt-void:{tracked.Id}:{tracked.RowVersion}", now));
                db.RevocationRecords.Add(new RevocationRecord { TargetType = "ExamAttempt", TargetId = tracked.Id, Scope = RevocationScope.VoidAttempt,
                    Reason = input.Reason.Trim(), ImpactJson = "{}", ActorId = currentUser.Id, ActorRole = "User", IdempotencyKey = IdempotencyKeyFactory.Create(currentUser, api, $"attempt-void:{attemptId}"), CreatedAt = now });
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, new { attemptId }), "exam_attempt.void", "ExamAttempt", attemptId,
                    after: new { tracked.Status, input.Reason }, reason: input.Reason.Trim());
                await db.SaveChangesAsync(); outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (ExamIdempotencyConflictException) { return Conflict(); }
        catch (DbUpdateConcurrencyException) { return Concurrency(); }
    }

    public async Task<int> AutoSubmitDueAsync(int limit = 100)
    {
        var now = clock.UtcNow;
        var due = await attempts.GetDueInProgressAsync(uow.Db, now - Grace, limit);
        foreach (var item in due)
        {
            try
            {
                await uow.ExecuteInTransactionAsync(async db =>
                {
                    var scopeInfo = await (from a in db.ExamAttempts.AsNoTracking()
                                           join asn in db.ExamAssignments.AsNoTracking() on a.AssignmentId equals asn.Id
                                           where a.Id == item.Id
                                           select new { asn.TaskId, AssignmentId = asn.Id }).FirstOrDefaultAsync();
                    if (scopeInfo is null) return;
                    await ExamLockService.LockTaskAsync(db, scopeInfo.TaskId);
                    await ExamLockService.LockAssignmentAsync(db, scopeInfo.AssignmentId);
                    await ExamLockService.LockAttemptAsync(db, item.Id);
                    var attempt = await attempts.GetTrackedAsync(db, item.Id);
                    if (attempt is null || attempt.Status != AttemptStatus.InProgress || now < attempt.DeadlineAt.Add(Grace)) return;
                    var assignment = await assignments.GetTrackedAsync(db, attempt.AssignmentId);
                    if (assignment is null) return;
                    var task = await db.ExamTasks.AsNoTracking().FirstAsync(x => x.Id == assignment.TaskId);
                    if (assignment.Status == AssignmentStatus.Revoked || task.Status == ExamTaskStatus.Revoked)
                    {
                        // 已撤回安排为数据库不可变终态，不得反向改为 Submitted；将残留进行中答卷安全作废以防后台任务循环报错。
                        attempt.Status = AttemptStatus.Voided;
                        attempt.VoidedAt = now;
                        attempt.VoidReason = "安排或任务已撤回，超时自动作废";
                        attempt.RowVersion++;
                        attempts.AddEvent(db, NewEvent(attempt.Id, null, "Voided", new { reason = attempt.VoidReason }, $"attempt-auto-void:{attempt.Id}:{attempt.RowVersion}", now));
                        await audit.WriteAsync(db, clock, new AuditContext(null, "System", null, null, null), "exam_attempt.auto_void_revoked", "ExamAttempt", attempt.Id,
                            after: new { attempt.Status, attempt.VoidedAt }, reason: attempt.VoidReason);
                        await db.SaveChangesAsync();
                        return;
                    }
                    await SubmitInternalAsync(db, attempt, assignment, task, true, null, "timeout", now);
                    await audit.WriteAsync(db, clock, new AuditContext(null, "System", null, null, null), "exam_attempt.auto_submit", "ExamAttempt", attempt.Id,
                        after: new { attempt.Status, attempt.SubmittedAt }, reason: "timeout");
                    await db.SaveChangesAsync();
                });
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // 考生交卷可能先赢得竞争；下一轮扫描会看到终态。
                logger.LogDebug(ex, "超时交卷与考生操作并发，答卷 {AttemptId} 留待下一轮", item.Id);
            }
            var taskId = await uow.Db.ExamAssignments.AsNoTracking()
                .Where(x => x.Id == uow.Db.ExamAttempts.Where(a => a.Id == item.Id).Select(a => a.AssignmentId).FirstOrDefault())
                .Select(x => (long?)x.TaskId).FirstOrDefaultAsync();
            if (taskId.HasValue) await results.TryAutoPublishAsync(taskId.Value);
        }
        return due.Count;
    }

    public async Task<AttemptSubmitResult> ForceSubmitInternalAsync(ExamDbContext db, ExamAttempt attempt, ExamAssignment assignment,
        ExamTask task, long? actorId, string reason, DateTime now, bool zeroScore = false)
        => await SubmitInternalAsync(db, attempt, assignment, task, false, actorId, reason, now, zeroScore: zeroScore);

    private async Task<AttemptSubmitResult> SubmitInternalAsync(ExamDbContext db, ExamAttempt attempt, ExamAssignment assignment,
        ExamTask task, bool automatic, long? actorId, string? reason, DateTime now, bool zeroScore = false)
    {
        if (attempt.Status != AttemptStatus.InProgress) throw new InvalidOperationException("ATTEMPT_NOT_IN_PROGRESS");
        var autoSubmitted = automatic && now >= attempt.DeadlineAt.Add(Grace);
        attempt.Status = autoSubmitted ? AttemptStatus.AutoSubmitted : AttemptStatus.Submitted;
        attempt.SubmittedAt = now; attempt.SubmitKey = $"submit:{attempt.Id}:{attempt.StartKey}";
        // 行政强制交卷保留 Submitted，由 ForceSubmitted 事件说明原因；AutoSubmitted 仅表示超时扫描。
        attempt.IsLate = autoSubmitted || now > attempt.DeadlineAt; attempt.RowVersion++;        assignment.Status = AssignmentStatus.Submitted; assignment.RowVersion++;
        var eventType = automatic ? "AutoSubmitted" : string.IsNullOrEmpty(reason) ? "Submitted" : "ForceSubmitted";
        attempts.AddEvent(db, NewEvent(attempt.Id, actorId, eventType, new { attempt.IsLate, reason }, $"attempt-submit:{attempt.Id}", now));
        await db.SaveChangesAsync();
        outbox.Add(db, "exam_attempt.submitted", "ExamAttempt", attempt.Id, new { attemptId = attempt.Id, assignmentId = assignment.Id, attempt.Status }, $"attempt-submitted:{attempt.Id}", now);
        await db.SaveChangesAsync();
        await GradeInternalAsync(db, attempt, assignment, task, now, zeroScore);
        return new AttemptSubmitResult(attempt.Id, attempt.Status, task.ResultStatus == "Published" ? attempt.Score : null,
            attempt.MaxScore, attempt.SubmittedAt!.Value, false);
    }

    private async Task GradeInternalAsync(ExamDbContext db, ExamAttempt attempt, ExamAssignment assignment, ExamTask task, DateTime now, bool zeroScore = false)
    {
        if (attempt.Status is not (AttemptStatus.Submitted or AttemptStatus.AutoSubmitted)) return;
        var stored = Deserialize<StoredPaper>(attempt.PaperSnapshotJson);
        var questions = stored.Sections.SelectMany(x => x.Questions).ToDictionary(x => x.QuestionVersionId);
        var answers = await attempts.GetAnswersTrackedAsync(db, attempt.Id);
        var sources = (await attempts.GetAttemptQuestionSourcesAsync(db, questions.Keys.ToArray())).ToDictionary(x => x.QuestionVersionId);
        var labels = (await attempts.GetOptionsAsync(db, questions.Keys.ToArray())).GroupBy(x => x.QuestionVersionId)
            .ToDictionary(x => x.Key, x => x.Select(o => o.Label).ToHashSet(StringComparer.Ordinal));
        foreach (var answer in answers)
        {
            if (!questions.TryGetValue(answer.QuestionVersionId, out var question) || !sources.TryGetValue(answer.QuestionVersionId, out var source)) continue;
            var invalid = ExamAlgorithms.ValidateAnswer(source.Type, answer.AnswerJson, labels.GetValueOrDefault(answer.QuestionVersionId, []));
            answer.Score = ExamAlgorithms.GradeObjective(source.Type, invalid.Value, source.CorrectJson, answer.MaxScore,
                zeroScore || (attempt.IsLate && task.LateScorePolicy == "Zero"));
            answer.GradingStatus = invalid.IsSuccess ? "Graded" : "Invalid";
            answer.UpdatedAt = now; answer.RowVersion++;
        }
        await db.SaveChangesAsync();
        attempt.AutoScore = answers.Sum(x => x.Score); attempt.Score = attempt.AutoScore;
        attempt.Status = AttemptStatus.Graded; attempt.GradedAt = now; attempt.GradedBy = null; attempt.RowVersion++;
        await db.SaveChangesAsync();
        var grants = await assignments.SumGrantedAttemptsAsync(db, assignment.Id, now);
        var limit = (assignment.AttemptLimitOverride ?? task.AttemptLimit) + grants;
        if (task.Status != ExamTaskStatus.Published) assignment.Status = AssignmentStatus.Graded;
        else if (assignment.AttemptsUsed + assignment.InheritedAttemptsUsed >= limit) assignment.Status = AssignmentStatus.Closed;
        else if (TimeFormat.ToLocal(now) >= assignment.AvailableTo) assignment.Status = AssignmentStatus.Expired;
        else assignment.Status = AssignmentStatus.Available;
        assignment.RowVersion++;
        attempts.AddEvent(db, NewEvent(attempt.Id, null, "Graded", new { attempt.AutoScore, attempt.MaxScore }, $"attempt-graded:{attempt.Id}", now));
        await db.SaveChangesAsync();
        outbox.Add(db, "exam_attempt.graded", "ExamAttempt", attempt.Id, new { attemptId = attempt.Id, score = attempt.Score }, $"attempt-graded:{attempt.Id}", now);
        await db.SaveChangesAsync();
    }

    private async Task<StartAttemptResult> BuildStartResultAsync(ExamDbContext db, ExamAttempt attempt)
    {
        var stored = Deserialize<StoredPaper>(attempt.PaperSnapshotJson);
        return new StartAttemptResult(attempt.Id, attempt.AssignmentId, attempt.AttemptNo, attempt.RowVersion, attempt.StartKey,
            attempt.StartedAt, attempt.DeadlineAt, clock.UtcNow, await BuildPaperAsync(db, stored, attempt.Id));
    }

    private async Task<AttemptPaperDto> BuildPaperAsync(ExamDbContext db, StoredPaper stored, long attemptId = 0)
    {
        var versionIds = stored.Sections.SelectMany(x => x.Questions).Select(x => x.QuestionVersionId).Distinct().ToArray();
        var sources = (await attempts.GetAttemptQuestionSourcesAsync(db, versionIds)).ToDictionary(x => x.QuestionVersionId);
        var options = (await attempts.GetOptionsAsync(db, versionIds)).GroupBy(x => x.QuestionVersionId).ToDictionary(x => x.Key, x => x.ToList());
        var answerMap = attemptId == 0 ? new Dictionary<long, string?>()
            : await db.AttemptAnswers.AsNoTracking().Where(x => x.AttemptId == attemptId).ToDictionaryAsync(x => x.QuestionVersionId, x => x.AnswerJson);
        return new AttemptPaperDto(stored.PaperVersionId, stored.AlgorithmVersion, stored.Sections.Select(section => new AttemptSectionDto(section.SectionId, section.Title,
            section.Questions.Select(question =>
            {
                var source = sources[question.QuestionVersionId];
                answerMap.TryGetValue(question.QuestionVersionId, out var answer);
                if (answer == "{}") answer = null;
                return question with { Type = source.Type, Stem = source.Stem,
                    Options = options.GetValueOrDefault(question.QuestionVersionId, []).Select(o => new AttemptOptionDto(o.Label, o.Content)).ToArray(), AnswerJson = answer };
            }).ToArray())).ToArray());
    }

    private Task<bool> CanAccessTaskAsync(ExamTask task)
        => scope.CanAccessTaskAsync(task.Id, task.DepartmentId, Permissions.ExamAttemptVoid);

    private long RequireCandidate()
    {
        if (!currentUser.IsAuthenticated || currentUser.Id is null) throw new UnauthorizedAccessException("请先登录");
        return currentUser.Id.Value;
    }
    private AttemptEvent NewEvent(long attemptId, long? actorId, string type, object payload, string key, DateTime? now = null)
        => new() { AttemptId = attemptId, ActorId = actorId, EventType = type, PayloadJson = Serialize(payload), EventKey = key, OccurredAt = now ?? clock.UtcNow };
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException("答卷快照格式无效");
    private static string MessageFor(string code) => code switch
    {
        "NOT_FOUND" => "答卷或安排不存在", "CONCURRENCY_CONFLICT" => "数据已被其他操作修改，请刷新后重试",
        "EXAM_TASK_NOT_PUBLISHED" => "考试任务当前未开放", "EXAM_TASK_REVOKED" => "考试任务已撤回",
        "ASSIGNMENT_REVOKED" => "考试安排已撤回",
        "RESULT_ALREADY_PUBLISHED" => "成绩已发布，请先撤回成绩后再操作",
        "ASSIGNMENT_WINDOW_NOT_OPEN" => "考试尚未开放", "ASSIGNMENT_WINDOW_CLOSED" => "考试时间窗已结束",
        "ATTEMPT_LIMIT_REACHED" => "考试次数已用尽", "ATTEMPT_ALREADY_SUBMITTED" => "答卷已交卷",
        "RETAKE_COOLDOWN_ACTIVE" => "重考冷却时间尚未结束", "RETAKE_APPROVAL_REQUIRED" => "重考需要管理员批准",
        "PAPER_SELECTION_FAILED" => "随机题池不足，请联系管理员", "INVALID_STATE_TRANSITION" => "当前状态不允许该操作",
        _ => "操作失败，请刷新后重试"
    };
    private static Result Failure() => Result.Fail("权限不足", "PERMISSION_DENIED");
    private static Result OutOfScope() => Result.Fail("无权操作该答卷", "OUT_OF_SCOPE");
    private static Result Conflict() => Result.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private static Result Concurrency() => Result.Fail("数据已被其他操作修改，请刷新后重试", "CONCURRENCY_CONFLICT");

    private sealed record StoredPaper(string SeedHex, string AlgorithmVersion, long PaperVersionId, IReadOnlyList<AttemptSectionDto> Sections);
}
