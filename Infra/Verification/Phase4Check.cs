using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using ExamOps.Model.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ExamOps.Infra.Verification;

/// <summary>
/// Phase 4 domain self-check. Every fixture and business write is isolated by one outer
/// transaction with commit:false; service transactions become savepoints in that transaction.
/// </summary>
public sealed class Phase4Check(
    IServiceScopeFactory scopeFactory,
    IHttpContextAccessor httpContextAccessor,
    IClock clock,
    IDbContextFactory<ExamDbContext> dbFactory)
{
    public async Task RunAsync()
    {
        await using var adminContext = await dbFactory.CreateDbContextAsync();
        var admin = await adminContext.Users.AsNoTracking()
            .Select(x => new { x.Id, x.Username })
            .SingleOrDefaultAsync(x => x.Username == "admin")
            ?? throw new InvalidOperationException("Phase 4 self-check requires the seeded admin user.");
        var previousContext = httpContextAccessor.HttpContext;
        void SetActor(long userId, string username)
        {
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new("examops_user_id", userId.ToString()), new(ClaimTypes.Name, username)], "ExamOps.Phase4Check"))
            };
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            httpContextAccessor.HttpContext = context;
        }
        var suffix = $"{clock.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var codePrefix = $"P4-{suffix}-";
        var tracePrefix = $"phase4-{suffix}";
        long[] taskIds = [];
        long[] assignmentIds = [];
        long[] attemptIds = [];

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var uow = services.GetRequiredService<IUnitOfWork>();
        try
        {
            SetActor(admin.Id, admin.Username);
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var now = clock.UtcNow;
                var checkClock = new MutableClock(now);
                var permissions = await db.Permissions.AsNoTracking().Select(x => x.Code).ToArrayAsync();
                var adminUser = new CheckCurrentUser(admin.Id, admin.Username, permissions, true, tracePrefix);
                var adminServices = BuildServices(services, uow, checkClock, adminUser);
                var root = await db.Departments.AsNoTracking().SingleAsync(x => x.Path == "001.");
                var departmentName = $"Phase4范围-{suffix}";
                Check((await adminServices.Organizations.CreateDepartmentAsync(departmentName, root.Id)).IsSuccess,
                    "create isolated Phase 4 department");
                var department = await db.Departments.AsNoTracking().SingleAsync(x => x.Name == departmentName);

                var candidateA = await CreateUserAsync(adminServices, db, department.Id, suffix, "a", "Active");
                var candidateB = await CreateUserAsync(adminServices, db, department.Id, suffix, "b", "Active");
                var disabledCandidate = await CreateUserAsync(adminServices, db, department.Id, suffix, "disabled", "Disabled");
                var atomicCandidate = await CreateUserAsync(adminServices, db, department.Id, suffix, "atomic", "Active");
                var candidateAServices = BuildServices(services, uow, checkClock,
                    new CheckCurrentUser(candidateA.Id, candidateA.Username, [], false, tracePrefix));
                var atomicServices = BuildServices(services, uow, checkClock,
                    new CheckCurrentUser(atomicCandidate.Id, atomicCandidate.Username, [], false, tracePrefix));

                var paper = await CreatePaperFixtureAsync(adminServices, db, department.Id, suffix);
                var createInput = NewTaskInput(codePrefix, "main", department.Id, paper.VersionId, now, 2, "Score");
                var createApi = Api(tracePrefix, "task-create-main");
                var created = await adminServices.Tasks.CreateAsync(createInput, createApi);
                Check(created.IsSuccess, "create exam task draft");
                var replay = await adminServices.Tasks.CreateAsync(createInput, createApi);
                Check(replay.IsSuccess && replay.Value == created.Value, "task create idempotency replay");
                var conflict = await adminServices.Tasks.CreateAsync(createInput with { Title = $"冲突任务-{suffix}" },
                    createApi with { RequestHash = "different-request" });
                CheckFailure(conflict, "IDEMPOTENCY_CONFLICT", "task create idempotency conflict");
                var duplicate = await adminServices.Tasks.CreateAsync(createInput, Api(tracePrefix, "task-create-duplicate"));
                CheckFailure(duplicate, "DUPLICATE_EXAM_TASK_CODE", "duplicate task code");
                Check(await db.ExamTasks.CountAsync(x => x.Code == createInput.Code) == 1, "task create replay has one side effect");

                var mainTaskId = created.Value!.TaskId;
                var mainDetail = await adminServices.Tasks.GetDetailAsync(mainTaskId)
                    ?? throw new InvalidOperationException("Main task detail missing.");
                Check(mainDetail.Status == "Draft", "new task is Draft");
                var publishApi = Api(tracePrefix, "task-publish-main");
                Check((await adminServices.Tasks.PublishAsync(mainTaskId, new(mainDetail.RowVersion), publishApi)).IsSuccess,
                    "publish exam task");
                Check((await adminServices.Tasks.PublishAsync(mainTaskId, new(mainDetail.RowVersion), publishApi)).IsSuccess,
                    "task publish idempotency replay");
                CheckFailure(await adminServices.Tasks.PublishAsync(mainTaskId, new(mainDetail.RowVersion),
                    publishApi with { RequestHash = "different-publish-request" }),
                    "IDEMPOTENCY_CONFLICT", "task publish idempotency conflict");
                mainDetail = await adminServices.Tasks.GetDetailAsync(mainTaskId)
                    ?? throw new InvalidOperationException("Published task detail missing.");
                Check(mainDetail.Status == "Published" && mainDetail.PublishedAt is not null,
                    "task publication lifecycle and audit");
                CheckFailure(await adminServices.Tasks.PublishAsync(mainTaskId, new(mainDetail.RowVersion)),
                    "EXAM_TASK_NOT_DRAFT", "published task cannot be published twice");
                Check(await db.AuditEvents.AnyAsync(x => x.Action == "exam_task.create" && x.EntityId == mainTaskId)
                    && await db.AuditEvents.AnyAsync(x => x.Action == "exam_task.publish" && x.EntityId == mainTaskId),
                    "task create and publish audit rows share the transaction");

                var skipBatch = await AssignAsync(adminServices, db, mainTaskId, suffix, "skip",
                    new AssignmentTargetInput([candidateB.Id], [], false, false, false));
                Check((await adminServices.Assignments.GetBatchDetailAsync(skipBatch.BatchId, 1, 10))?.Status == "Completed",
                    "initial assignment succeeds");
                var target = new AssignmentTargetInput([candidateA.Id, candidateB.Id, disabledCandidate.Id], [], false, false, false);
                var preview = await adminServices.Assignments.PreviewAsync(mainTaskId, target);
                Check(preview.IsSuccess && preview.Value!.TotalCount == 3
                    && preview.Value.EligibleCount == 1 && preview.Value.SkippedCount == 1 && preview.Value.FailedCount == 1
                    && preview.Value.Rows.Single(x => x.UserId == candidateA.Id).Outcome == "Eligible"
                    && preview.Value.Rows.Single(x => x.UserId == candidateB.Id).Outcome == "Skipped"
                    && preview.Value.Rows.Single(x => x.UserId == disabledCandidate.Id).Outcome == "Failed",
                    "assignment preview distinguishes eligible, skipped and failed targets");
                var batch = await AssignAsync(adminServices, db, mainTaskId, suffix, "mixed", target);
                var batchDetail = await adminServices.Assignments.GetBatchDetailAsync(batch.BatchId, 1, 10)
                    ?? throw new InvalidOperationException("Assignment batch detail missing.");
                Check(batchDetail.Status == "PartiallyFailed" && batchDetail.SuccessCount == 1
                    && batchDetail.FailedCount == 1 && batchDetail.SkippedCount == 1, "mixed assignment batch counts");
                var disabledRow = await db.Users.AsNoTracking().SingleAsync(x => x.Id == disabledCandidate.Id);
                Check((await adminServices.Users.SetStatusAsync(disabledCandidate.Id, "Active", disabledRow.RowVersion)).IsSuccess,
                    "enable failed assignment target for retry");
                batchDetail = await adminServices.Assignments.GetBatchDetailAsync(batch.BatchId, 1, 10)
                    ?? throw new InvalidOperationException("Assignment batch detail missing before retry.");
                var retryApi = Api(tracePrefix, "assignment-retry-mixed");
                var retryRowVersion = batchDetail.RowVersion;
                var retried = await adminServices.Assignments.RetryBatchAsync(batch.BatchId, retryRowVersion, retryApi);
                Check(retried.IsSuccess && retried.Value == batch, "batch retry succeeds and is idempotent");
                batchDetail = await adminServices.Assignments.GetBatchDetailAsync(batch.BatchId, 1, 10)
                    ?? throw new InvalidOperationException("Assignment batch detail missing after retry.");
                Check(batchDetail.Status == "Completed" && batchDetail.SuccessCount == 2
                    && batchDetail.FailedCount == 0 && batchDetail.SkippedCount == 1, "retry reopens only failed rows");
                Check((await adminServices.Assignments.RetryBatchAsync(batch.BatchId, retryRowVersion, retryApi)).IsSuccess,
                    "batch retry replay");
                CheckFailure(await adminServices.Assignments.RetryBatchAsync(batch.BatchId, retryRowVersion + 1,
                    retryApi with { RequestHash = "different-retry-request" }),
                    "IDEMPOTENCY_CONFLICT", "batch retry conflict");
                Check(await db.ExamAssignments.CountAsync(x => x.TaskId == mainTaskId) == 3
                    && await db.ExamAssignments.CountAsync(x => x.TaskId == mainTaskId && x.UserId == candidateA.Id) == 1,
                    "assignment retry does not duplicate active assignments");

                var mainAssignment = await db.ExamAssignments.AsNoTracking()
                    .SingleAsync(x => x.TaskId == mainTaskId && x.UserId == candidateA.Id);
                SetActor(candidateA.Id, candidateA.Username);
                var startApi = Api(tracePrefix, "attempt-start-main");
                var started = await candidateAServices.Attempts.StartAsync(mainAssignment.Id, new(mainAssignment.RowVersion), startApi);
                Check(started.IsSuccess, $"start fixed/random attempt [{started.Code}: {started.Error}]");
                var start = started.Value ?? throw new InvalidOperationException("Started attempt missing.");
                var startReplay = await candidateAServices.Attempts.StartAsync(mainAssignment.Id, new(mainAssignment.RowVersion), startApi);
                Check(startReplay.IsSuccess && startReplay.Value!.AttemptId == start.AttemptId
                    && startReplay.Value.StartKey == start.StartKey && startReplay.Value.AttemptNo == start.AttemptNo
                    && startReplay.Value.Paper.Sections.SelectMany(x => x.Questions).Select(x => x.QuestionVersionId)
                        .SequenceEqual(start.Paper.Sections.SelectMany(x => x.Questions).Select(x => x.QuestionVersionId)),
                    "start idempotency replay returns the same attempt and paper");
                CheckFailure(await candidateAServices.Attempts.StartAsync(mainAssignment.Id,
                    new(mainAssignment.RowVersion + 1), startApi with { RequestHash = "different-start-request" }),
                    "IDEMPOTENCY_CONFLICT", "start idempotency conflict");
                var currentAssignment = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == mainAssignment.Id);
                var resumed = await candidateAServices.Attempts.StartAsync(mainAssignment.Id, new(currentAssignment.RowVersion));
                Check(resumed.IsSuccess && resumed.Value!.AttemptId == start.AttemptId && resumed.Value.AttemptNo == 1,
                    "disconnect resume returns the in-progress attempt");
                Check(await db.ExamAttempts.CountAsync(x => x.AssignmentId == mainAssignment.Id) == 1,
                    "resume does not consume another attempt");
                await CheckSnapshotAsync(db, start.AttemptId, start.Paper, paper);

                var questions = start.Paper.Sections.SelectMany(x => x.Questions).ToArray();
                var singleVersion = questions.Single(x => x.Type == "SingleChoice" && paper.FixedVersionIds.Contains(x.QuestionVersionId)).QuestionVersionId;
                var multipleVersion = questions.Single(x => x.Type == "MultipleChoice").QuestionVersionId;
                CheckFailure(await candidateAServices.Attempts.SaveAnswersAsync(start.AttemptId,
                    new() { RowVersion = start.RowVersion, Answers = [new(long.MaxValue, "{}")] }),
                    "ANSWER_FORMAT_INVALID", "answer outside snapshot is rejected");
                CheckFailure(await candidateAServices.Attempts.SaveAnswersAsync(start.AttemptId,
                    new() { RowVersion = start.RowVersion, Answers = [new(multipleVersion, "{\"values\":[\"A\",\"A\"]}")] }),
                    "ANSWER_FORMAT_INVALID", "duplicate multiple-choice option is rejected");
                var correctAnswers = await CorrectAnswersAsync(db, questions.Select(x => x.QuestionVersionId).ToArray());
                var saveApi = Api(tracePrefix, "answer-save-main");
                Check((await candidateAServices.Attempts.SaveAnswersAsync(start.AttemptId,
                    new() { RowVersion = start.RowVersion, Answers = correctAnswers }, saveApi)).IsSuccess, "save objective answers");
                Check((await candidateAServices.Attempts.SaveAnswersAsync(start.AttemptId,
                    new() { RowVersion = start.RowVersion, Answers = correctAnswers }, saveApi)).IsSuccess, "answer save idempotency replay");
                CheckFailure(await candidateAServices.Attempts.SaveAnswersAsync(start.AttemptId,
                    new() { RowVersion = start.RowVersion, Answers = correctAnswers },
                    saveApi with { RequestHash = "different-save-request" }),
                    "IDEMPOTENCY_CONFLICT", "answer save idempotency conflict");
                var recovered = await candidateAServices.Attempts.GetDetailAsync(start.AttemptId)
                    ?? throw new InvalidOperationException("Attempt recovery detail missing.");
                Check(recovered.Paper.Sections.SelectMany(x => x.Questions).All(x => x.AnswerJson is not null),
                    "saved answers recover after reconnect");
                var multipleAnswer = await db.AttemptAnswers.AsNoTracking().SingleAsync(x => x.AttemptId == start.AttemptId
                    && x.QuestionVersionId == multipleVersion);
                var canonicalMultiple = JsonSerializer.Deserialize<Dictionary<string, string[]>>(multipleAnswer.AnswerJson!,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Check(canonicalMultiple?.TryGetValue("values", out var selectedValues) == true
                    && selectedValues!.SequenceEqual(["A", "B"]),
                    $"multiple-choice answer is canonicalized as a set [{multipleAnswer.AnswerJson}]");

                var attemptBeforeBoundary = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == start.AttemptId);
                checkClock.UtcNow = attemptBeforeBoundary.DeadlineAt.AddSeconds(30);
                Check((await candidateAServices.Attempts.SaveAnswersAsync(start.AttemptId,
                    new() { RowVersion = attemptBeforeBoundary.RowVersion, Answers = [new(singleVersion, "{\"value\":\"A\"}")] },
                    Api(tracePrefix, "answer-save-grace"))).IsSuccess, "answer save at exactly 30-second grace boundary");
                var attemptAfterBoundary = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == start.AttemptId);
                checkClock.UtcNow = attemptAfterBoundary.DeadlineAt.AddSeconds(31);
                CheckFailure(await candidateAServices.Attempts.SaveAnswersAsync(start.AttemptId,
                    new() { RowVersion = attemptAfterBoundary.RowVersion, Answers = [new(singleVersion, "{\"value\":\"A\"}")] }),
                    "ANSWER_FROZEN", "answer save after grace is frozen");
                var frozenAttempt = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == start.AttemptId);
                Check(frozenAttempt.RowVersion == attemptAfterBoundary.RowVersion, "rejected late save has no row-version side effect");
                checkClock.UtcNow = attemptAfterBoundary.DeadlineAt.AddSeconds(30);
                var submitApi = Api(tracePrefix, "attempt-submit-main");
                var submitted = await candidateAServices.Attempts.SubmitAsync(start.AttemptId,
                    new(attemptAfterBoundary.RowVersion), submitApi);
                Check(submitted.IsSuccess && submitted.Value!.Status == "Graded" && submitted.Value.Score == null
                    && submitted.Value.MaxScore == 20m, "submit auto-grades while hiding unpublished candidate scores");
                var graded = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == start.AttemptId);
                Check(graded.IsLate && graded.SubmittedAt == checkClock.UtcNow && graded.Score == 20m,
                    "late submit inside grace keeps Score policy points");
                Check(await db.AttemptAnswers.CountAsync(x => x.AttemptId == start.AttemptId && x.GradingStatus == "Graded" && x.Score == 5m) == 4,
                    "all objective questions receive exact full or zero scores");
                Check((await candidateAServices.Attempts.SubmitAsync(start.AttemptId,
                    new(attemptAfterBoundary.RowVersion), submitApi)).Value == submitted.Value,
                    "submit idempotency replay");
                CheckFailure(await candidateAServices.Attempts.SubmitAsync(start.AttemptId,
                    new(attemptAfterBoundary.RowVersion), submitApi with { RequestHash = "different-submit-request" }),
                    "IDEMPOTENCY_CONFLICT", "submit idempotency conflict");
                CheckFailure(await candidateAServices.Attempts.SubmitAsync(start.AttemptId, new(graded.RowVersion),
                    Api(tracePrefix, "attempt-submit-main-again")), "ATTEMPT_ALREADY_SUBMITTED", "second submit API is rejected");
                CheckFailure(await candidateAServices.Attempts.SaveAnswersAsync(start.AttemptId,
                    new() { RowVersion = graded.RowVersion, Answers = [new(singleVersion, "{\"value\":\"B\"}")] }),
                    "ANSWER_FROZEN", "graded answers are immutable through the domain API");
                await CheckAttemptFactsAsync(db, start.AttemptId, tracePrefix);

                var afterFirst = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == mainAssignment.Id);
                var secondStart = await candidateAServices.Attempts.StartAsync(mainAssignment.Id, new(afterFirst.RowVersion));
                Check(secondStart.IsSuccess && secondStart.Value!.AttemptNo == 2, "second attempt consumes the configured limit");
                var secondAttempt = secondStart.Value ?? throw new InvalidOperationException("Second attempt missing.");
                var secondSubmit = await candidateAServices.Attempts.SubmitAsync(secondAttempt.AttemptId,
                    new(secondAttempt.RowVersion));
                Check(secondSubmit.IsSuccess && secondSubmit.Value!.Score == null
                    && (await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == secondAttempt.AttemptId)).Score == 0m,
                    "unanswered second attempt grades zero while hiding unpublished score");
                var exhausted = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == mainAssignment.Id);
                Check(exhausted.Status == "Closed", "attempt limit closes assignment");
                CheckFailure(await candidateAServices.Attempts.StartAsync(mainAssignment.Id, new(exhausted.RowVersion)),
                    "ATTEMPT_LIMIT_REACHED", "attempt limit blocks a third attempt");

                var assignedOtherTask = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "assigned-other",
                    department.Id, paper.VersionId, checkClock.UtcNow, 1, "Score");
                var assignedOtherBatch = await AssignAsync(adminServices, db, assignedOtherTask, suffix, "assigned-other",
                    new AssignmentTargetInput([candidateA.Id], [], false, false, false));
                var assignedOtherAssignmentId = await GetSingleAssignmentIdAsync(assignedOtherBatch, db);
                SetActor(admin.Id, admin.Username);
                Check((await adminServices.Tasks.AddStaffAsync(mainTaskId,
                    new(candidateB.Id, "Grader"), Api(tracePrefix, "staff-role-mismatch"))).IsSuccess,
                    "assign staff with a role that does not match the operator");
                var mismatchStaff = await db.ExamTaskStaffs.AsNoTracking().SingleAsync(x => x.TaskId == mainTaskId
                    && x.UserId == candidateB.Id);
                var assignedPermissions = new[]
                {
                    Permissions.ExamTaskRead, Permissions.ExamAssignmentRead,
                    Permissions.ExamAssignmentRevoke, Permissions.ExamAssignmentGrantRetake
                };
                var assignedUser = new CheckCurrentUser(candidateB.Id, candidateB.Username, assignedPermissions, false, tracePrefix,
                    [new(RoleCodes.Invigilator, null, "Assigned", assignedPermissions)]);
                var assignedServices = BuildServices(services, uow, checkClock, assignedUser);
                Check(await assignedServices.Tasks.GetDetailAsync(mainTaskId) is null,
                    "Assigned scope does not leak a task through a mismatched staff role");
                Check((await adminServices.Tasks.RemoveStaffAsync(mainTaskId, mismatchStaff.Id,
                    Api(tracePrefix, "staff-remove-mismatch"))).IsSuccess, "remove mismatched staff fixture");
                Check((await adminServices.Tasks.AddStaffAsync(mainTaskId,
                    new(candidateB.Id, "Invigilator"), Api(tracePrefix, "staff-role-match"))).IsSuccess,
                    "assign matching Invigilator staff role");
                var assignedTasks = await assignedServices.Tasks.GetPagedAsync(new(null, null, null, 1, 100));
                Check(assignedTasks.TotalCount == 1 && assignedTasks.Items.Single().Id == mainTaskId
                    && await assignedServices.Tasks.GetDetailAsync(mainTaskId) is not null
                    && await assignedServices.Tasks.GetDetailAsync(assignedOtherTask) is null,
                    "Assigned scope lists and reads only explicitly assigned tasks");
                var assignedMainRow = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.TaskId == mainTaskId
                    && x.UserId == candidateB.Id);
                var assignedOtherRow = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == assignedOtherAssignmentId);
                Check(await assignedServices.Assignments.GetRowAsync(assignedMainRow.Id) is not null,
                    "Assigned scope reads an assignment in its task");
                Check(await assignedServices.Assignments.GetRowAsync(assignedOtherRow.Id) is null,
                    "Assigned scope hides an assignment in another task");
                Check((await assignedServices.Assignments.GetPagedAsync(new(mainTaskId, null, null, 1, 10))).TotalCount == 3,
                    "Assigned scope pages all rows of its task");
                Check((await assignedServices.Assignments.GetPagedAsync(new(assignedOtherTask, null, null, 1, 10))).TotalCount == 0,
                    "Assigned scope excludes rows of another task");
                CheckFailure(await assignedServices.Assignments.RevokeAsync(assignedOtherRow.Id,
                    new(assignedOtherRow.RowVersion, "Assigned 越界撤回自查", RevocationScope.KeepScore),
                    Api(tracePrefix, "assigned-revoke-denied")), "NOT_FOUND", "unassigned task revocation hides resource existence");
                Check((await assignedServices.Assignments.GrantRetakeAsync(assignedMainRow.Id,
                    new(assignedMainRow.RowVersion, 1, "Assigned 范围内加次自查", checkClock.UtcNow.AddDays(1)),
                    Api(tracePrefix, "assigned-grant"))).IsSuccess, "Assigned operator can grant retake within task scope");
                var assignedMainAfterGrant = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == assignedMainRow.Id);
                Check((await assignedServices.Assignments.RevokeAsync(assignedMainRow.Id,
                    new(assignedMainAfterGrant.RowVersion, "Assigned 范围内撤回自查", RevocationScope.KeepScore),
                    Api(tracePrefix, "assigned-revoke"))).IsSuccess, "Assigned operator can revoke within task scope");
                Check((await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == assignedMainRow.Id)).Status == "Revoked",
                    "Assigned positive mutation changes only the authorized assignment");

                var grantTask = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "retake-grant", department.Id,
                    paper.VersionId, checkClock.UtcNow, 2, "Score");
                var grantAssignment = await AssignAsync(adminServices, db, grantTask, suffix, "retake-grant",
                    new AssignmentTargetInput([candidateA.Id], [], false, false, false));
                var grantAssignmentId = await GetSingleAssignmentIdAsync(grantAssignment, db);
                var grantRow = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == grantAssignmentId);
                var grantFirstStart = await candidateAServices.Attempts.StartAsync(grantRow.Id, new(grantRow.RowVersion));
                Check(grantFirstStart.IsSuccess, "start first attempt before retake grant");
                var grantFirstAttempt = grantFirstStart.Value ?? throw new InvalidOperationException("Grant first attempt missing.");
                Check((await candidateAServices.Attempts.SubmitAsync(grantFirstAttempt.AttemptId,
                    new(grantFirstAttempt.RowVersion))).IsSuccess, "submit first attempt before retake grant");
                var grantAvailable = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == grantAssignmentId);
                Check(grantAvailable.Status == "Available", "assignment remains available before granted retake");
                SetActor(admin.Id, admin.Username);
                var grantApi = Api(tracePrefix, "retake-grant-main");
                var grantInput = new GrantRetakeInput(grantAvailable.RowVersion, 1, "Phase4 追加一次重考", checkClock.UtcNow.AddDays(1));
                Check((await adminServices.Assignments.GrantRetakeAsync(grantAssignmentId, grantInput, grantApi)).IsSuccess,
                    "grant extra retake");
                Check((await adminServices.Assignments.GrantRetakeAsync(grantAssignmentId, grantInput, grantApi)).IsSuccess,
                    "retake grant idempotency replay");
                CheckFailure(await adminServices.Assignments.GrantRetakeAsync(grantAssignmentId,
                    grantInput with { ExtraAttempts = 2 }, grantApi with { RequestHash = "different-grant-request" }),
                    "IDEMPOTENCY_CONFLICT", "retake grant conflict");
                Check(await db.RetakeGrants.CountAsync(x => x.AssignmentId == grantAssignmentId) == 1,
                    "retake grant replay has one side effect");
                var grantedAssignment = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == grantAssignmentId);
                SetActor(candidateA.Id, candidateA.Username);
                var secondGrantedStart = await candidateAServices.Attempts.StartAsync(grantAssignmentId, new(grantedAssignment.RowVersion));
                Check(secondGrantedStart.IsSuccess && secondGrantedStart.Value!.AttemptNo == 2, "start second attempt after grant");
                var secondGrantedAttempt = secondGrantedStart.Value ?? throw new InvalidOperationException("Second granted attempt missing.");
                Check((await candidateAServices.Attempts.SubmitAsync(secondGrantedAttempt.AttemptId,
                    new(secondGrantedAttempt.RowVersion))).IsSuccess, "submit second attempt after grant");
                var beforeThird = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == grantAssignmentId);
                var thirdStart = await candidateAServices.Attempts.StartAsync(grantAssignmentId, new(beforeThird.RowVersion));
                Check(thirdStart.IsSuccess && thirdStart.Value!.AttemptNo == 3, "granted retake starts attempt three");
                var thirdAttempt = thirdStart.Value ?? throw new InvalidOperationException("Third attempt missing.");
                var thirdQuestions = thirdAttempt.Paper.Sections.SelectMany(x => x.Questions).ToArray();
                var thirdAnswers = await CorrectAnswersAsync(db, thirdQuestions.Select(x => x.QuestionVersionId).ToArray());
                Check((await candidateAServices.Attempts.SaveAnswersAsync(thirdAttempt.AttemptId,
                    new() { RowVersion = thirdAttempt.RowVersion, Answers = thirdAnswers })).IsSuccess, "save third-attempt answers");
                var thirdBeforeSubmit = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == thirdAttempt.AttemptId);
                var thirdSubmit = await candidateAServices.Attempts.SubmitAsync(thirdAttempt.AttemptId,
                    new(thirdBeforeSubmit.RowVersion));
                Check(thirdSubmit.IsSuccess && thirdSubmit.Value!.Score == null
                    && (await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == thirdAttempt.AttemptId)).Score == 20m,
                    "granted retake auto-grades while hiding unpublished score");

                var lateTask = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "late-zero", department.Id,
                    paper.VersionId, checkClock.UtcNow, 1, "Zero");
                var lateAssignment = await AssignAsync(adminServices, db, lateTask, suffix, "late-zero",
                    new AssignmentTargetInput([candidateA.Id], [], false, false, false));
                var lateAssignmentId = await GetSingleAssignmentIdAsync(lateAssignment, db);
                var lateAssignmentRow = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == lateAssignmentId);
                SetActor(candidateA.Id, candidateA.Username);
                var lateStart = await candidateAServices.Attempts.StartAsync(lateAssignmentRow.Id, new(lateAssignmentRow.RowVersion));
                Check(lateStart.IsSuccess, "start late-zero attempt");
                var lateQuestions = lateStart.Value!.Paper.Sections.SelectMany(x => x.Questions).ToArray();
                Check((await candidateAServices.Attempts.SaveAnswersAsync(lateStart.Value.AttemptId,
                    new() { RowVersion = lateStart.Value.RowVersion,
                        Answers = await CorrectAnswersAsync(db, lateQuestions.Select(x => x.QuestionVersionId).ToArray()) })).IsSuccess,
                    "save correct answers before late-zero submit");
                var lateBeforeSubmit = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == lateStart.Value.AttemptId);
                checkClock.UtcNow = lateBeforeSubmit.DeadlineAt.AddSeconds(1);
                var lateSubmit = await candidateAServices.Attempts.SubmitAsync(lateStart.Value.AttemptId,
                    new(lateBeforeSubmit.RowVersion));
                var lateGraded = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == lateStart.Value.AttemptId);
                Check(lateSubmit.IsSuccess && lateGraded.IsLate && lateGraded.Score == 0m
                    && await db.AttemptAnswers.AllAsync(x => x.AttemptId != lateStart.Value.AttemptId || x.Score == 0m),
                    "late-zero policy zeroes otherwise correct answers inside grace");

                var timeoutTask = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "timeout", department.Id,
                    paper.VersionId, checkClock.UtcNow, 1, "Score");
                var timeoutAssignment = await AssignAsync(adminServices, db, timeoutTask, suffix, "timeout",
                    new AssignmentTargetInput([candidateA.Id], [], false, false, false));
                var timeoutAssignmentRow = await db.ExamAssignments.AsNoTracking()
                    .SingleAsync(x => x.TaskId == timeoutTask && x.UserId == candidateA.Id);
                var timeoutStart = await candidateAServices.Attempts.StartAsync(timeoutAssignmentRow.Id,
                    new(timeoutAssignmentRow.RowVersion));
                Check(timeoutStart.IsSuccess, "start timeout attempt");
                var timeoutAttemptStart = timeoutStart.Value ?? throw new InvalidOperationException("Timeout attempt missing.");
                var timeoutAttempt = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == timeoutAttemptStart.AttemptId);
                checkClock.UtcNow = timeoutAttempt.DeadlineAt.AddSeconds(29);
                var beforeTimeout = await candidateAServices.Attempts.GetDetailAsync(timeoutAttemptStart.AttemptId);
                Check(beforeTimeout is { Status: "InProgress", CanSave: true, CanSubmit: true }
                    && !await db.AttemptEvents.AnyAsync(x => x.AttemptId == timeoutAttemptStart.AttemptId && x.EventType == "AutoSubmitted"),
                    "timeout scanner does not submit before the grace boundary");
                checkClock.UtcNow = timeoutAttempt.DeadlineAt.AddSeconds(30);
                // ponytail: the existing public scanner selects the global due queue; the outer rollback
                // isolates all rows it touches, but it can briefly lock unrelated due attempts.
                await candidateAServices.Attempts.AutoSubmitDueAsync();
                var autoGraded = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == timeoutAttemptStart.AttemptId);
                Check(autoGraded.Status == "Graded" && autoGraded.IsLate && autoGraded.SubmittedAt == checkClock.UtcNow
                    && await db.AttemptEvents.AnyAsync(x => x.AttemptId == autoGraded.Id && x.EventType == "AutoSubmitted")
                    && await db.AuditEvents.AnyAsync(x => x.Action == "exam_attempt.auto_submit" && x.EntityId == autoGraded.Id),
                    "timeout at 30 seconds records AutoSubmitted and audit before grading");

                var forceTask = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "revoke-force", department.Id,
                    paper.VersionId, checkClock.UtcNow, 2, "Score");
                var forceAssignment = await AssignAsync(adminServices, db, forceTask, suffix, "revoke-force",
                    new AssignmentTargetInput([candidateA.Id], [], false, false, false));
                var forceAssignmentId = await GetSingleAssignmentIdAsync(forceAssignment, db);
                var forceRow = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == forceAssignmentId);
                var forceStart = await candidateAServices.Attempts.StartAsync(forceRow.Id, new(forceRow.RowVersion));
                Check(forceStart.IsSuccess, "start revocation race attempt");
                var forceQuestions = forceStart.Value!.Paper.Sections.SelectMany(x => x.Questions).ToArray();
                Check((await candidateAServices.Attempts.SaveAnswersAsync(forceStart.Value.AttemptId,
                    new() { RowVersion = forceStart.Value.RowVersion,
                        Answers = await CorrectAnswersAsync(db, forceQuestions.Select(x => x.QuestionVersionId).ToArray()) })).IsSuccess,
                    "save wins before revocation");
                var forceBeforeRevoke = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == forceStart.Value.AttemptId);
                var forceAssignmentBeforeRevoke = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == forceRow.Id);
                SetActor(admin.Id, admin.Username);
                Check((await adminServices.Assignments.RevokeAsync(forceRow.Id,
                    new(forceAssignmentBeforeRevoke.RowVersion, "Phase4 撤回竞争自查，强制交卷", RevocationScope.ForceSubmit),
                    Api(tracePrefix, "assignment-revoke-force"))).IsSuccess, "revocation force-submits after save");
                var forceAfterRevoke = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == forceStart.Value.AttemptId);
                Check(forceAfterRevoke.Status == "Graded" && forceAfterRevoke.Score == 20m
                    && await db.AttemptEvents.AnyAsync(x => x.AttemptId == forceAfterRevoke.Id && x.EventType == "ForceSubmitted")
                    && (await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == forceRow.Id)).Status == "Revoked",
                    "save then revoke serializes to graded and revoked");
                SetActor(candidateA.Id, candidateA.Username);
                CheckFailure(await candidateAServices.Attempts.SaveAnswersAsync(forceStart.Value.AttemptId,
                    new() { RowVersion = forceBeforeRevoke.RowVersion, Answers = [new(forceQuestions[0].QuestionVersionId, null)] }),
                    "CONCURRENCY_CONFLICT", "stale save loses to revocation");
                CheckFailure(await candidateAServices.Attempts.SubmitAsync(forceStart.Value.AttemptId,
                    new(forceBeforeRevoke.RowVersion)), "CONCURRENCY_CONFLICT", "stale submit loses to revocation");
                CheckFailure(await candidateAServices.Attempts.SubmitAsync(forceStart.Value.AttemptId,
                    new(forceAfterRevoke.RowVersion)), "ATTEMPT_ALREADY_SUBMITTED", "fresh submit after revocation is frozen");

                var voidScoreTask = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "revoke-void-score",
                    department.Id, paper.VersionId, checkClock.UtcNow, 2, "Score");
                var voidScoreAssignment = await AssignAsync(adminServices, db, voidScoreTask, suffix, "revoke-void-score",
                    new AssignmentTargetInput([candidateA.Id], [], false, false, false));
                var voidScoreAssignmentId = await GetSingleAssignmentIdAsync(voidScoreAssignment, db);
                var voidScoreRow = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == voidScoreAssignmentId);
                var voidScoreStart = await candidateAServices.Attempts.StartAsync(voidScoreRow.Id, new(voidScoreRow.RowVersion));
                Check(voidScoreStart.IsSuccess, "start VoidScore revocation attempt");
                var voidScoreQuestions = voidScoreStart.Value!.Paper.Sections.SelectMany(x => x.Questions).ToArray();
                var voidScoreAnswers = await CorrectAnswersAsync(db, voidScoreQuestions.Select(x => x.QuestionVersionId).ToArray());
                Check((await candidateAServices.Attempts.SaveAnswersAsync(voidScoreStart.Value.AttemptId,
                    new() { RowVersion = voidScoreStart.Value.RowVersion, Answers = voidScoreAnswers })).IsSuccess,
                    "save correct answers before VoidScore revocation");
                var answersBeforeVoid = await db.AttemptAnswers.AsNoTracking().Where(x => x.AttemptId == voidScoreStart.Value.AttemptId)
                    .OrderBy(x => x.QuestionVersionId).Select(x => new { x.QuestionVersionId, x.AnswerJson }).ToArrayAsync();
                var voidScoreAssignmentBeforeRevoke = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == voidScoreRow.Id);
                SetActor(admin.Id, admin.Username);
                Check((await adminServices.Assignments.RevokeAsync(voidScoreRow.Id,
                    new(voidScoreAssignmentBeforeRevoke.RowVersion, "Phase4 作废得分但保留答卷自查", RevocationScope.VoidScore),
                    Api(tracePrefix, "assignment-revoke-void-score"))).IsSuccess, "VoidScore revocation succeeds");
                var voidScoreAttempt = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == voidScoreStart.Value.AttemptId);
                var answersAfterVoid = await db.AttemptAnswers.AsNoTracking().Where(x => x.AttemptId == voidScoreStart.Value.AttemptId)
                    .OrderBy(x => x.QuestionVersionId).Select(x => new { x.QuestionVersionId, x.AnswerJson, x.Score, x.GradingStatus }).ToArrayAsync();
                Check(voidScoreAttempt.Status == "Graded" && voidScoreAttempt.Score == 0m && voidScoreAttempt.AutoScore == 0m
                    && !voidScoreAttempt.IsLate && voidScoreAttempt.SubmittedAt == checkClock.UtcNow
                    && answersBeforeVoid.Select(x => (x.QuestionVersionId, x.AnswerJson))
                        .SequenceEqual(answersAfterVoid.Select(x => (x.QuestionVersionId, x.AnswerJson)))
                    && answersAfterVoid.Length == 4 && answersAfterVoid.All(x => x.Score == 0m && x.GradingStatus == "Graded")
                    && (await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == voidScoreRow.Id)).Status == "Revoked"
                    && await db.RevocationRecords.AnyAsync(x => x.TargetType == "ExamAssignment"
                        && x.TargetId == voidScoreRow.Id && x.Scope == RevocationScope.VoidScore)
                    && await db.AttemptEvents.AnyAsync(x => x.AttemptId == voidScoreAttempt.Id && x.EventType == "ForceSubmitted")
                    && await db.AttemptEvents.AnyAsync(x => x.AttemptId == voidScoreAttempt.Id && x.EventType == "Graded"),
                    "VoidScore preserves answers and grading facts, grades zero, and records a non-late forced submission");

                var submitFirstTask = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "submit-revoke", department.Id,
                    paper.VersionId, checkClock.UtcNow, 2, "Score");
                var submitFirstAssignment = await AssignAsync(adminServices, db, submitFirstTask, suffix, "submit-revoke",
                    new AssignmentTargetInput([candidateA.Id], [], false, false, false));
                var submitFirstAssignmentId = await GetSingleAssignmentIdAsync(submitFirstAssignment, db);
                var submitFirstRow = await db.ExamAssignments.AsNoTracking()
                    .SingleAsync(x => x.Id == submitFirstAssignmentId);
                var submitFirstStart = await candidateAServices.Attempts.StartAsync(submitFirstRow.Id, new(submitFirstRow.RowVersion));
                Check(submitFirstStart.IsSuccess, "start submit-first revocation race");
                var submitFirstAttempt = submitFirstStart.Value ?? throw new InvalidOperationException("Submit-first attempt missing.");
                var submitFirstBefore = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == submitFirstAttempt.AttemptId);
                Check((await candidateAServices.Attempts.SubmitAsync(submitFirstAttempt.AttemptId,
                    new(submitFirstBefore.RowVersion))).IsSuccess, "submit wins before revocation");
                var submitFirstAssignmentBeforeRevoke = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == submitFirstRow.Id);
                SetActor(admin.Id, admin.Username);
                Check((await adminServices.Assignments.RevokeAsync(submitFirstRow.Id,
                    new(submitFirstAssignmentBeforeRevoke.RowVersion, "Phase4 交卷后撤回自查，保留成绩", RevocationScope.KeepScore),
                    Api(tracePrefix, "assignment-revoke-after-submit"))).IsSuccess, "revocation after submit succeeds");
                Check((await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == submitFirstAttempt.AttemptId)).Status == "Graded"
                    && (await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == submitFirstRow.Id)).Status == "Revoked",
                    "submit then revoke serializes without resurrecting the assignment");

                var taskRevoke = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "task-revoke", department.Id,
                    paper.VersionId, checkClock.UtcNow, 2, "Score");
                var taskRevokeAssignment = await AssignAsync(adminServices, db, taskRevoke, suffix, "task-revoke",
                    new AssignmentTargetInput([candidateA.Id], [], false, false, false));
                var taskRevokeAssignmentId = await GetSingleAssignmentIdAsync(taskRevokeAssignment, db);
                var taskRevokeRow = await db.ExamAssignments.AsNoTracking()
                    .SingleAsync(x => x.Id == taskRevokeAssignmentId);
                SetActor(candidateA.Id, candidateA.Username);
                var taskRevokeStart = await candidateAServices.Attempts.StartAsync(taskRevokeRow.Id, new(taskRevokeRow.RowVersion));
                Check(taskRevokeStart.IsSuccess, "start task-revocation attempt");
                var taskRevokeDetail = await adminServices.Tasks.GetDetailAsync(taskRevoke)
                    ?? throw new InvalidOperationException("Task revocation detail missing.");
                SetActor(admin.Id, admin.Username);
                Check((await adminServices.Tasks.RevokeAsync(taskRevoke,
                    new(taskRevokeDetail.RowVersion, "Phase4 整任务撤回自查，强制交卷", taskRevokeDetail.Title, RevocationScope.ForceSubmit),
                    Api(tracePrefix, "task-revoke"))).IsSuccess, "revoke published task with in-progress attempt");
                Check((await db.ExamTasks.AsNoTracking().SingleAsync(x => x.Id == taskRevoke)).Status == "Revoked"
                    && (await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == taskRevokeRow.Id)).Status == "Revoked"
                    && (await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == taskRevokeStart.Value!.AttemptId)).Status == "Graded"
                    && await db.OutboxMessages.AnyAsync(x => x.Type == "exam_task.revoked" && x.AggregateId == taskRevoke)
                    && await db.OutboxMessages.AnyAsync(x => x.Type == "exam_assignment.revoked" && x.AggregateId == taskRevokeRow.Id),
                    "task revocation, forced submission, audit and outbox commit together");

                var eventRow = await db.AttemptEvents.AsNoTracking().FirstAsync(x => x.AttemptId == autoGraded.Id);
                await ExpectGuardAsync(db, "UPDATE attempt_event SET payload_json = {0}::jsonb WHERE id = {1}", "{}", eventRow.Id);
                await ExpectGuardAsync(db, "DELETE FROM attempt_event WHERE id = {0}", eventRow.Id);

                var atomicTask = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "atomic", department.Id,
                    paper.VersionId, checkClock.UtcNow, 1, "Score");
                var atomicAssignment = await AssignAsync(adminServices, db, atomicTask, suffix, "atomic",
                    new AssignmentTargetInput([atomicCandidate.Id], [], false, false, false));
                var atomicAssignmentId = await GetSingleAssignmentIdAsync(atomicAssignment, db);
                var atomicRow = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == atomicAssignmentId);
                SetActor(atomicCandidate.Id, atomicCandidate.Username);
                long? rolledBackAttemptId = null;
                try
                {
                    await uow.ExecuteInTransactionAsync(async _ =>
                    {
                        var atomicStart = await atomicServices.Attempts.StartAsync(atomicRow.Id, new(atomicRow.RowVersion));
                        Check(atomicStart.IsSuccess, "start inside atomic rollback probe");
                        rolledBackAttemptId = atomicStart.Value!.AttemptId;
                        throw new InvalidOperationException("phase4-atomic-rollback-probe");
                    });
                }
                catch (InvalidOperationException ex) when (ex.Message == "phase4-atomic-rollback-probe") { }
                var rolledBackId = rolledBackAttemptId ?? throw new InvalidOperationException("Atomic rollback probe did not start.");
                Check(!await db.ExamAttempts.AnyAsync(x => x.Id == rolledBackId),
                    "attempt, answers and events roll back together");
                Check(!await db.AuditEvents.AnyAsync(x => x.EntityType == "ExamAttempt" && x.EntityId == rolledBackId)
                    && !await db.OutboxMessages.AnyAsync(x => x.AggregateType == "ExamAttempt" && x.AggregateId == rolledBackId),
                    "audit and outbox roll back with the failed attempt transaction");

                var outboxConsumer = new OutboxConsumerService(uow, new AuditRepository(), checkClock,
                    services.GetRequiredService<ILogger<OutboxConsumerService>>());
                var outboxRepository = new OutboxRepository();
                var replayEventKey = $"phase4-outbox-replay-{suffix}";
                outboxRepository.Add(db, "exam_task.published", "ExamTask", mainTaskId, new { taskId = mainTaskId },
                    replayEventKey, checkClock.UtcNow);
                await db.SaveChangesAsync();
                await outboxConsumer.ProcessDueAsync(500);
                var replayMessage = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.EventKey == replayEventKey);
                Check(replayMessage.Status == "Sent" && replayMessage.SentAt == checkClock.UtcNow
                    && await db.AuditEvents.CountAsync(x => x.EventKey == "consume:" + replayEventKey) == 1,
                    "outbox consumer sends a due event and records one idempotent consumption");
                // 模拟“消费副作用已提交、确认状态未持久化”的可重放窗口；不重开 Sent 终态。
                var redeliveryEventKey = $"phase4-outbox-redelivery-{suffix}";
                await new AuditRepository().WriteAsync(db, checkClock,
                    new AuditContext(null, "System", null, null, null), "outbox.consume", "ExamTask", mainTaskId,
                    after: new { taskId = mainTaskId }, eventKey: "consume:" + redeliveryEventKey);
                outboxRepository.Add(db, "exam_task.published", "ExamTask", mainTaskId, new { taskId = mainTaskId },
                    redeliveryEventKey, checkClock.UtcNow);
                await db.SaveChangesAsync();
                await outboxConsumer.ProcessDueAsync(500);
                Check((await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.EventKey == redeliveryEventKey)).Status == "Sent"
                    && await db.AuditEvents.CountAsync(x => x.EventKey == "consume:" + redeliveryEventKey) == 1,
                    "at-least-once redelivery does not duplicate the consumption side effect");

                var unknownEventKey = $"phase4-outbox-dead-{suffix}";
                outboxRepository.Add(db, "phase4.unknown", "ExamTask", mainTaskId, new { fixture = true },
                    unknownEventKey, checkClock.UtcNow);
                await db.SaveChangesAsync();
                for (var expectedRetry = 1; expectedRetry <= 5; expectedRetry++)
                {
                    await outboxConsumer.ProcessDueAsync(500);
                    var failedMessage = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.EventKey == unknownEventKey);
                    Check(failedMessage.RetryCount == expectedRetry && failedMessage.LockedAt is null
                        && failedMessage.LastError is not null, $"outbox failure records retry {expectedRetry}");
                    if (expectedRetry < 5)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Min(300, 15 * (1 << (expectedRetry - 1))));
                        Check(failedMessage.Status == "Pending" && failedMessage.NextAttemptAt == checkClock.UtcNow.Add(delay),
                            $"outbox retry {expectedRetry} uses exponential backoff");
                        checkClock.UtcNow = failedMessage.NextAttemptAt;
                    }
                    else
                    {
                        Check(failedMessage.Status == "Dead" && failedMessage.SentAt is null,
                            "outbox reaches terminal Dead after five failed attempts");
                    }
                }

                var staleEventKey = $"phase4-outbox-stale-{suffix}";
                var staleMessage = new OutboxMessage
                {
                    EventKey = staleEventKey, Type = "exam_task.published", AggregateType = "ExamTask",
                    AggregateId = mainTaskId, PayloadJson = "{\"taskId\":" + mainTaskId + "}", Status = "Pending",
                    NextAttemptAt = checkClock.UtcNow.AddDays(1),
                    CreatedAt = checkClock.UtcNow.AddMinutes(-4), RowVersion = 1
                };
                db.OutboxMessages.Add(staleMessage);
                await db.SaveChangesAsync();
                staleMessage.Status = "Processing";
                staleMessage.LockedAt = checkClock.UtcNow.AddMinutes(-3);
                staleMessage.RowVersion++;
                await db.SaveChangesAsync();
                await outboxConsumer.ProcessDueAsync(500);
                Check((await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.EventKey == staleEventKey)) is
                    { Status: "Sent", LockedAt: null, SentAt: not null }, "expired processing lease is recovered and acknowledged");

                taskIds = await db.ExamTasks.Where(x => x.Code.StartsWith(codePrefix)).Select(x => x.Id).ToArrayAsync();
                assignmentIds = await db.ExamAssignments.Where(x => taskIds.Contains(x.TaskId)).Select(x => x.Id).ToArrayAsync();
                attemptIds = await db.ExamAttempts.Where(x => assignmentIds.Contains(x.AssignmentId)).Select(x => x.Id).ToArrayAsync();
            }, commit: false);

            await using var verify = await dbFactory.CreateDbContextAsync();
            Check(!await verify.ExamTasks.AnyAsync(x => x.Code.StartsWith(codePrefix)), "all task fixtures roll back");
            Check(!await verify.Departments.AnyAsync(x => x.Name == $"Phase4范围-{suffix}"), "department fixture rolls back");
            Check(!await verify.Users.AnyAsync(x => x.Username.StartsWith($"p4_{suffix}_")), "user fixtures roll back");
            Check(!await verify.QuestionBanks.AnyAsync(x => x.Name == $"Phase4题库-{suffix}"), "question bank fixture rolls back");
            Check(!await verify.Papers.AnyAsync(x => x.Title == $"Phase4试卷-{suffix}"), "paper fixture rolls back");
            Check(!await verify.AuditEvents.AnyAsync(x => x.TraceId == tracePrefix || (x.TraceId != null && x.TraceId.StartsWith(tracePrefix))
                || (x.EntityType == "ExamTask" && taskIds.Contains(x.EntityId))
                || (x.EntityType == "ExamAssignment" && assignmentIds.Contains(x.EntityId))
                || (x.EntityType == "ExamAttempt" && attemptIds.Contains(x.EntityId))),
                "all fixture audit rows roll back");
            Check(!await verify.OutboxMessages.AnyAsync(x =>
                (x.AggregateType == "ExamTask" && taskIds.Contains(x.AggregateId))
                || (x.AggregateType == "ExamAssignment" && assignmentIds.Contains(x.AggregateId))
                || (x.AggregateType == "ExamAttempt" && attemptIds.Contains(x.AggregateId))),
                "all fixture outbox rows roll back");
            Console.WriteLine($"Phase 4 self-check passed: {taskIds.Length} task fixtures and all related data rolled back; "
                + "task/assignment/attempt idempotency, preview/retry, fixed/random snapshots, grading, grace/late/timeout, "
                + "serialized revocation races, VoidScore, Assigned scope, append-only guards, outbox retry/dead-letter/stale-lease/idempotency "
                + "and audit/outbox atomicity checked.");
        }
        finally
        {
            httpContextAccessor.HttpContext = previousContext;
        }
    }

    /// <summary>
    /// Cross-connection start race. This mode commits fixtures and therefore only accepts a disposable
    /// database explicitly marked by EXAMOPS_PHASE4_DISPOSABLE_DB=1; the database name must use the
    /// examops_phase4_ prefix so the production database cannot be selected accidentally.
    /// </summary>
    public async Task RunConcurrencyAsync()
    {
        if (Environment.GetEnvironmentVariable("EXAMOPS_PHASE4_DISPOSABLE_DB") != "1")
            throw new InvalidOperationException("并发自查必须显式设置 EXAMOPS_PHASE4_DISPOSABLE_DB=1，并连接 examops_phase4_ 前缀的一次性数据库。");
        await using var guardContext = await dbFactory.CreateDbContextAsync();
        var databaseName = guardContext.Database.GetDbConnection().Database;
        Check(databaseName.StartsWith("examops_phase4_", StringComparison.Ordinal),
            "并发自查只允许 examops_phase4_ 前缀的一次性数据库");
        var admin = await guardContext.Users.AsNoTracking().Select(x => new { x.Id, x.Username })
            .SingleOrDefaultAsync(x => x.Username == "admin")
            ?? throw new InvalidOperationException("并发自查数据库缺少种子管理员。");
        var suffix = $"{clock.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var codePrefix = $"P4-RACE-{suffix}-";
        var tracePrefix = $"phase4-race-{suffix}";
        long assignmentId;
        long assignmentUserId;
        long assignmentRowVersion;

        using (var setupScope = scopeFactory.CreateScope())
        {
            var setupServices = setupScope.ServiceProvider;
            var setupUow = setupServices.GetRequiredService<IUnitOfWork>();
            var setupClock = new MutableClock(clock.UtcNow);
            var permissions = await setupUow.Db.Permissions.AsNoTracking().Select(x => x.Code).ToArrayAsync();
            var adminServices = BuildServices(setupServices, setupUow, setupClock,
                new CheckCurrentUser(admin.Id, admin.Username, permissions, true, tracePrefix));
            var db = setupUow.Db;
            var root = await db.Departments.AsNoTracking().SingleAsync(x => x.Path == "001.");
            Check((await adminServices.Organizations.CreateDepartmentAsync($"Phase4并发范围-{suffix}", root.Id)).IsSuccess,
                "create disposable concurrency department");
            var department = await db.Departments.AsNoTracking().SingleAsync(x => x.Name == $"Phase4并发范围-{suffix}");
            var candidate = await CreateUserAsync(adminServices, db, department.Id, suffix, "race", "Active");
            var paper = await CreatePaperFixtureAsync(adminServices, db, department.Id, $"race-{suffix}");
            var taskId = await CreatePublishedTaskAsync(adminServices, db, codePrefix, "race", department.Id,
                paper.VersionId, setupClock.UtcNow, 1, "Score");
            var batch = await AssignAsync(adminServices, db, taskId, suffix, "race",
                new AssignmentTargetInput([candidate.Id], [], false, false, false));
            assignmentId = await GetSingleAssignmentIdAsync(batch, db);
            var setupAssignment = await db.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == assignmentId);
            assignmentUserId = setupAssignment.UserId;
            assignmentRowVersion = setupAssignment.RowVersion;
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Result<StartAttemptResult>> StartOnIndependentConnectionAsync(int request)
        {
            using var requestScope = scopeFactory.CreateScope();
            var requestServices = requestScope.ServiceProvider;
            var requestUow = requestServices.GetRequiredService<IUnitOfWork>();
            var requestUser = new CheckCurrentUser(assignmentUserId, $"p4_{suffix}_race", [], false, tracePrefix);
            var candidateServices = BuildServices(requestServices, requestUow, new MutableClock(clock.UtcNow), requestUser);
            await gate.Task;
            return await candidateServices.Attempts.StartAsync(assignmentId, new(assignmentRowVersion),
                Api(tracePrefix, $"attempt-start-race-{request}"));
        }

        var firstRequest = StartOnIndependentConnectionAsync(1);
        var secondRequest = StartOnIndependentConnectionAsync(2);
        gate.SetResult();
        var outcomes = await Task.WhenAll(firstRequest, secondRequest);
        var successes = outcomes.Where(x => x.IsSuccess).Select(x => x.Value!).ToArray();
        Check(successes.Length is 1 or 2
            && outcomes.All(x => x.IsSuccess || x.Code == "CONCURRENCY_CONFLICT")
            && successes.All(x => x.AttemptId == successes[0].AttemptId && x.StartKey == successes[0].StartKey),
            "parallel starts serialize to one attempt or a concurrency response");
        var attemptId = successes[0].AttemptId;
        await using var verify = await dbFactory.CreateDbContextAsync();
        Check(await verify.ExamAttempts.CountAsync(x => x.AssignmentId == assignmentId) == 1
            && await verify.AttemptAnswers.CountAsync(x => x.AttemptId == attemptId) == 4
            && await verify.AttemptEvents.CountAsync(x => x.AttemptId == attemptId && x.EventType == "Started") == 1
            && await verify.AuditEvents.CountAsync(x => x.Action == "exam_attempt.start" && x.EntityId == attemptId) == 1
            && await verify.OutboxMessages.CountAsync(x => x.Type == "exam_attempt.started" && x.AggregateId == attemptId) == 1,
            "parallel starts consume one attempt and write one answer set, event, audit and outbox row");
        var persisted = await verify.ExamAssignments.AsNoTracking().SingleAsync(x => x.Id == assignmentId);
        Check(persisted.AttemptsUsed == 1 && persisted.InheritedAttemptsUsed == 0 && persisted.Status == "InProgress",
            "parallel starts consume the attempt limit exactly once");
        Console.WriteLine("Phase 4 cross-connection concurrency self-check passed on disposable database " + databaseName
            + ": two independent starts serialized to one attempt, one charge and one lifecycle write set.");
    }

    private static Services BuildServices(IServiceProvider provider, IUnitOfWork uow, IClock checkClock, ICurrentUser currentUser)
    {
        var departments = new DepartmentRepository(uow);
        var users = new UserRepository(uow);
        var roles = new RoleRepository(uow);
        var questions = new QuestionRepository(uow);
        var papers = new PaperRepository(uow);
        var assignments = new AssignmentRepository(checkClock);
        var attempts = new AttemptRepository(checkClock);
        var audit = new AuditRepository();
        var outbox = new OutboxRepository();
        var scope = new DataScopeService(currentUser, departments, uow);
        var idempotency = new ExamIdempotencyService(currentUser, audit);
        var resultService = new ResultReleaseService(uow, new ExamTaskRepository(), audit, outbox, idempotency, scope, currentUser, checkClock,
            provider.GetRequiredService<ILogger<ResultReleaseService>>());
        var attemptService = new ExamAttemptService(uow, attempts, assignments, new ExamTaskRepository(), audit, outbox, idempotency, scope, currentUser, checkClock,
            resultService, provider.GetRequiredService<ILogger<ExamAttemptService>>());
        var assignmentService = new AssignmentService(uow, assignments, new ExamTaskRepository(), attemptService,
            scope, audit, outbox, idempotency, currentUser, checkClock);
        var taskService = new ExamTaskService(uow, new ExamTaskRepository(), assignmentService,
            scope, audit, outbox, idempotency, resultService, currentUser, checkClock);
        var questionService = new QuestionService(uow, questions, departments, scope, audit, currentUser, checkClock);
        var paperService = new PaperService(uow, papers, questions, scope, audit, currentUser, checkClock);
        var userService = new UserService(uow, users, departments, roles, scope, audit, currentUser, checkClock,
            provider.GetRequiredService<PasswordHasher<AppUser>>());
        var organizationService = new OrganizationService(uow, departments, users, roles, audit, currentUser, scope, checkClock);
        return new(taskService, assignmentService, attemptService, questionService, paperService, userService, organizationService);
    }

    private static async Task<AppUser> CreateUserAsync(Services services, ExamDbContext db, long departmentId,
        string suffix, string discriminator, string status)
    {
        var username = $"p4_{suffix}_{discriminator}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var created = await services.Users.CreateAsync(new UserEditInput(username, $"Phase4考生-{discriminator}", null,
            status, departmentId, password, [], [departmentId]));
        Check(created.IsSuccess, $"create {status} Phase 4 candidate fixture");
        return await db.Users.AsNoTracking().SingleAsync(x => x.Username == username);
    }

    private static async Task<PaperFixture> CreatePaperFixtureAsync(Services services, ExamDbContext db,
        long departmentId, string suffix)
    {
        var bankName = $"Phase4题库-{suffix}";
        var bank = await services.Questions.CreateBankAsync(bankName, departmentId, false);
        Check(bank.IsSuccess, "create Phase 4 question bank");
        var category = await services.Questions.CreateCategoryAsync(bank.Value!, "Phase4分类", null);
        Check(category.IsSuccess, "create Phase 4 question category");
        var versionIds = new List<long>();
        var definitions = new (string Type, string Value, IReadOnlyList<string> Values, bool TrueFalse,
            IReadOnlyList<QuestionOptionInput> Options)[]
        {
            ("SingleChoice", "A", [], true, [new QuestionOptionInput("A", "甲", true, 1), new QuestionOptionInput("B", "乙", false, 2)]),
            ("MultipleChoice", "", ["A", "B"], false, [new QuestionOptionInput("A", "甲", true, 1), new QuestionOptionInput("B", "乙", true, 2), new QuestionOptionInput("C", "丙", false, 3)]),
            ("TrueFalse", "", [], true, []),
            ("SingleChoice", "B", [], false, [new QuestionOptionInput("A", "甲", false, 1), new QuestionOptionInput("B", "乙", true, 2)]),
            ("TrueFalse", "", [], false, [])
        };
        for (var i = 0; i < definitions.Length; i++)
        {
            var (type, value, values, trueFalse, options) = definitions[i];
            var created = await services.Questions.CreateAsync(new QuestionEditInput(bank.Value!, $"P4-{suffix}-{i + 1}",
                type, $"Phase4题目 {i + 1}", "Easy", 5m, null, options, [], [category.Value!], value, values, trueFalse));
            Check(created.IsSuccess, $"create Phase 4 question {i + 1}");
            var questionDetail = await services.Questions.GetDetailAsync(created.Value!.QuestionId)
                ?? throw new InvalidOperationException("Question detail missing.");
            Check((await services.Questions.PublishAsync(created.Value.QuestionId, created.Value.DraftVersionId,
                questionDetail.RowVersion)).IsSuccess, $"publish Phase 4 question {i + 1}");
            versionIds.Add(created.Value.DraftVersionId);
        }

        var title = $"Phase4试卷-{suffix}";
        var createdPaper = await services.Papers.CreateAsync(new CreatePaperInput(title, departmentId, 60));
        Check(createdPaper.IsSuccess, "create Phase 4 paper");
        var paperId = createdPaper.Value!.PaperId;
        var paperVersionId = createdPaper.Value.VersionId;
        var detail = await services.Papers.GetDetailAsync(paperId, paperVersionId)
            ?? throw new InvalidOperationException("Paper detail missing.");
        var fixedSection = new PaperSectionInput(0, "固定题", 1, "Fixed", null, 5m, null, null, null, null,
            versionIds.Take(3).ToArray());
        var randomSectionInput = new PaperSectionInput(0, "随机题", 2, "Random", 1, 5m, bank.Value, category.Value, "SingleChoice", "Easy", []);
        Check((await services.Papers.UpdateDraftAsync(paperId, paperVersionId, detail.RowVersion, detail.VersionRowVersion,
            new(detail.VersionTitle, 60, [fixedSection, randomSectionInput]))).IsSuccess, "save fixed and random paper sections");
        detail = await services.Papers.GetDetailAsync(paperId, paperVersionId)
            ?? throw new InvalidOperationException("Paper detail missing after update.");
        Check((await services.Papers.ApplyBlueprintAsync(paperId, paperVersionId,
            detail.Sections.Single(x => x.SelectionMode == "Random").Id, detail.RowVersion, detail.VersionRowVersion)).IsSuccess,
            "generate persisted random candidate pool");
        detail = await services.Papers.GetDetailAsync(paperId, paperVersionId)
            ?? throw new InvalidOperationException("Paper detail missing after blueprint.");
        var poolCount = detail.Preview.RandomPoolCount;
        var selectedCount = detail.Preview.SelectedRandomCount;
        Check(poolCount == 2 && selectedCount == 1, "random pool preview is exact");
        Check((await services.Papers.PublishAsync(paperId, new(paperVersionId, detail.VersionRowVersion), detail.RowVersion)).IsSuccess,
            "publish Phase 4 paper");
        return new(paperVersionId, versionIds.Take(3).ToArray(), versionIds.Skip(3).ToArray());
    }

    private static CreateExamTaskInput NewTaskInput(string prefix, string name, long departmentId, long paperVersionId,
        DateTime now, int attemptLimit, string latePolicy)
    {
        var localNow = TimeFormat.ToLocal(now);
        return new($"{prefix}{name}", $"Phase4任务-{name}", null, departmentId, DateOnly.FromDateTime(localNow),
            paperVersionId, localNow.AddHours(-1), localNow.AddDays(1), 1, attemptLimit, "Latest", "None", 0,
            10m, "Immediate", latePolicy);
    }

    private static async Task<long> CreatePublishedTaskAsync(Services services, ExamDbContext db, string prefix,
        string name, long departmentId, long paperVersionId, DateTime now, int attemptLimit, string latePolicy)
    {
        var created = await services.Tasks.CreateAsync(NewTaskInput(prefix, name, departmentId, paperVersionId, now,
            attemptLimit, latePolicy), Api($"phase4-{name}-{Guid.NewGuid():N}", $"task-create-{name}"));
        Check(created.IsSuccess, $"create {name} task");
        var detail = await services.Tasks.GetDetailAsync(created.Value!.TaskId)
            ?? throw new InvalidOperationException("Task detail missing.");
        Check((await services.Tasks.PublishAsync(detail.Id, new(detail.RowVersion),
            Api($"phase4-{name}-{Guid.NewGuid():N}", $"task-publish-{name}"))).IsSuccess, $"publish {name} task");
        return detail.Id;
    }

    private static async Task<AssignmentMutationResult> AssignAsync(Services services, ExamDbContext db, long taskId,
        string suffix, string name, AssignmentTargetInput target)
    {
        var task = await db.ExamTasks.AsNoTracking().SingleAsync(x => x.Id == taskId);
        var result = await services.Assignments.AssignAsync(taskId, new(target, task.RowVersion),
            Api($"phase4-{suffix}", $"assignment-{name}"));
        Check(result.IsSuccess, $"assign {name} batch");
        return result.Value!;
    }

    private static Task<long> GetSingleAssignmentIdAsync(AssignmentMutationResult result, ExamDbContext db)
        => db.ExamAssignments.AsNoTracking().Where(x => x.BatchId == result.BatchId).Select(x => x.Id).SingleAsync();

    private static async Task<List<SaveAnswerItemInput>> CorrectAnswersAsync(ExamDbContext db, IReadOnlyList<long> versionIds)
    {
        var sources = await (from version in db.QuestionVersions.AsNoTracking()
                             join question in db.Questions.AsNoTracking() on version.QuestionId equals question.Id
                             where versionIds.Contains(version.Id)
                             select new { version.Id, question.Type, version.AnswerJson }).ToArrayAsync();
        return sources.Select(source =>
        {
            var answer = source.Type == "MultipleChoice"
                ? "{\"values\":[" + string.Join(',', JsonSerializer.Deserialize<JsonElement>(source.AnswerJson)
                    .GetProperty("values").EnumerateArray().Select(x => x.GetString()).Reverse()
                    .Select(x => JsonSerializer.Serialize(x))) + "]}"
                : source.AnswerJson;
            return new SaveAnswerItemInput(source.Id, answer);
        }).ToList();
    }

    private static async Task CheckSnapshotAsync(ExamDbContext db, long attemptId, AttemptPaperDto paper, PaperFixture fixture)
    {
        var attempt = await db.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        using var snapshot = JsonDocument.Parse(attempt.PaperSnapshotJson);
        var root = snapshot.RootElement;
        Check(root.GetProperty("algorithmVersion").GetString() == ExamAlgorithms.AlgorithmVersion,
            "snapshot records the selection algorithm version");
        var seedHex = root.GetProperty("seedHex").GetString() ?? "";
        Check(Regex.IsMatch(seedHex, "^[0-9a-f]{64}$"), "snapshot records a 32-byte lowercase seed");
        var snapshotQuestions = root.GetProperty("sections").EnumerateArray()
            .SelectMany(x => x.GetProperty("questions").EnumerateArray()).ToArray();
        Check(snapshotQuestions.All(x => !x.TryGetProperty("answerJson", out var answer) || answer.ValueKind == JsonValueKind.Null)
            && snapshotQuestions.All(x => !x.TryGetProperty("correctJson", out _) && !x.TryGetProperty("isCorrect", out _)
                && !x.TryGetProperty("analysis", out _)), "persisted candidate snapshot contains no answer key or analysis");
        Check(paper.Sections.SelectMany(x => x.Questions).All(x => x.AnswerJson is null),
            $"start DTO contains no candidate answers [{string.Join(",", paper.Sections.SelectMany(x => x.Questions).Select(x => $"{x.QuestionVersionId}:{x.AnswerJson ?? "null"}"))}]");
        var questions = paper.Sections.SelectMany(x => x.Questions).ToArray();
        Check(questions.Length == 4 && fixture.FixedVersionIds.All(id => questions.Any(x => x.QuestionVersionId == id))
            && questions.Count(x => fixture.RandomVersionIds.Contains(x.QuestionVersionId)) == 1,
            "fixed questions and one random candidate are selected");
        var repository = new AttemptRepository(new MutableClock(attempt.StartedAt));
        var (sections, pool) = await repository.GetPaperSourcesAsync(db, paper.PaperVersionId);
        var selection = ExamAlgorithms.SelectPaper(Convert.FromHexString(seedHex),
            sections.Select(x => new ExamPaperSectionSource(x.Id, x.Title, x.SortOrder, x.SelectionMode, x.PickCount, x.ScorePerQuestion)).ToArray(),
            pool.Select(x => new ExamPaperQuestionSource(x.PaperQuestionId, x.SectionId, x.QuestionVersionId, x.QuestionId, x.Score, x.IsPool)).ToArray());
        Check(selection.IsSuccess && selection.AlgorithmVersion == ExamAlgorithms.AlgorithmVersion
            && selection.Questions.Select(x => x.QuestionVersionId).SequenceEqual(questions.Select(x => x.QuestionVersionId)),
            "persisted seed reproduces the exact selected paper");
    }

    private static async Task CheckAttemptFactsAsync(ExamDbContext db, long attemptId, string tracePrefix)
    {
        foreach (var action in new[] { "exam_attempt.start", "exam_attempt.save_answers", "exam_attempt.submit" })
            Check(await db.AuditEvents.AnyAsync(x => x.Action == action && x.EntityId == attemptId),
                $"audit row for {action}");
        foreach (var type in new[] { "exam_attempt.started", "exam_attempt.submitted", "exam_attempt.graded" })
            Check(await db.OutboxMessages.AnyAsync(x => x.Type == type && x.AggregateType == "ExamAttempt" && x.AggregateId == attemptId),
                $"outbox row for {type}");
        Check(await db.AttemptEvents.CountAsync(x => x.AttemptId == attemptId && x.EventType == "Started") == 1
            && await db.AttemptEvents.CountAsync(x => x.AttemptId == attemptId && x.EventType == "Submitted") == 1
            && await db.AttemptEvents.CountAsync(x => x.AttemptId == attemptId && x.EventType == "Graded") == 1,
            "attempt event lifecycle is singular");
    }

    private static async Task ExpectGuardAsync(ExamDbContext db, string sql, params object[] parameters)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = Regex.Replace(sql, @"\{(\d+)\}", "@p$1");
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@p{i}";
            parameter.Value = parameters[i];
            command.Parameters.Add(parameter);
        }
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Append-only guard check requires the outer transaction.");
        await transaction.CreateSavepointAsync("phase4_guard");
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.InsufficientPrivilege)
        {
            // 应用角色没有 UPDATE/DELETE 权限；触发器另行保护所有者。两者都证明 append-only 边界不可绕过。
            // PostgreSQL aborts the transaction on a trigger exception; restore the savepoint
            // before any subsequent fixture query or write.
            await transaction.RollbackToSavepointAsync("phase4_guard");
            await transaction.ReleaseSavepointAsync("phase4_guard");
            return;
        }
        await transaction.ReleaseSavepointAsync("phase4_guard");
        throw new InvalidOperationException($"Phase 4 self-check failed: append-only guard accepted {sql}");
    }

    private static ApiAuditMetadata Api(string trace, string name)
        => new($"p4-{name}-{Guid.NewGuid():N}", $"hash-{name}", "POST", $"/api/phase4-check/{name}");

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Phase 4 self-check failed: {name}");
    }

    private static void CheckFailure(Result result, string code, string name)
        => Check(!result.IsSuccess && result.Code == code, name);

    private sealed class MutableClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }

    private sealed class CheckCurrentUser(long userId, string username, IReadOnlyList<string> permissions,
        bool administrator, string trace, IReadOnlyList<RoleAssignmentData>? roles = null) : ICurrentUser
    {
        private readonly string[] _permissions = permissions.Distinct(StringComparer.Ordinal).ToArray();
        private readonly IReadOnlyList<RoleAssignmentData> _roles = roles ?? [];
        public long? Id => userId;
        public string? DisplayName => username;
        public string? Username => username;
        public bool IsAuthenticated => true;
        public bool MustChangePassword => false;
        public IReadOnlyList<RoleAssignmentData> Roles => administrator
            ? [new(RoleCodes.PlatformAdmin, null, "All", _permissions)] : _roles;
        public IReadOnlyList<DepartmentAccessData> Departments => [];
        public Task LoadAsync() => Task.CompletedTask;
        public bool HasPermission(string permission) => _permissions.Contains(permission, StringComparer.Ordinal);
        public bool HasAnyPermission(params string[] permissions) => permissions.Any(HasPermission);
        public bool HasRole(string roleCode) => administrator
            ? roleCode == RoleCodes.PlatformAdmin
            : _roles.Any(x => x.Code == roleCode);
        public AuditContext GetAuditContext() => new(userId, administrator ? RoleCodes.PlatformAdmin : RoleCodes.Candidate,
            trace, "127.0.0.1", "ExamOps.Phase4Check");
    }

    private sealed record Services(
        ExamTaskService Tasks,
        AssignmentService Assignments,
        ExamAttemptService Attempts,
        QuestionService Questions,
        PaperService Papers,
        UserService Users,
        OrganizationService Organizations);

    private sealed record PaperFixture(long VersionId, IReadOnlyList<long> FixedVersionIds, IReadOnlyList<long> RandomVersionIds);
}
