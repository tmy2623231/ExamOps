using System.IO.Compression;
using System.Security.Claims;
using System.Xml.Linq;
using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Infra.Export;
using ExamOps.Infra.Storage;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamOps.Infra.Verification;

/// <summary>
/// Phase 5 review and scoring self-check. All fixtures and writes roll back with the outer transaction.
/// </summary>
public sealed class Phase5Check(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IDbContextFactory<ExamDbContext> dbFactory,
    ILogger<Phase5Check> logger)
{
    public async Task RunAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var admin = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Username == "admin")
            ?? throw new InvalidOperationException("Phase 5 self-check requires the seeded admin user.");
        var permissions = await db.Permissions.AsNoTracking().Select(x => x.Code).ToArrayAsync();
        var suffix = $"p5-{clock.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        var uow = provider.GetRequiredService<IUnitOfWork>();
        var adminUser = new CheckUser(admin.Id, admin.Username, permissions, true, suffix);
        var adminReviews = BuildReviewService(uow, adminUser);
        var adminResults = BuildResultService(uow, adminUser);
        var adminAttempts = BuildAttemptService(provider, uow, adminUser, adminResults);
        RunFinalScoreCheck();
        RunReportFileCheck();
        var reportRoot = Path.Combine(provider.GetRequiredService<IHostEnvironment>().ContentRootPath,
            "Data", "tmp", $"phase5-reports-{Guid.NewGuid():N}");
        var storage = new FileStorage(Path.Combine(reportRoot, "uploads"), Path.Combine(reportRoot, "tmp"),
            Path.Combine(reportRoot, "export"), clock);
        var adminReports = BuildReportService(uow, adminUser, storage);
        try
        {
            await uow.ExecuteInTransactionAsync(async (ExamDbContext tx) =>
        {
            var now = clock.UtcNow;
            var root = await tx.Departments.AsNoTracking().SingleAsync(x => x.Path == "001.");
            var department = await CreateDepartmentAsync(tx, root.Id, suffix, now);
            var otherDepartment = await CreateDepartmentAsync(tx, root.Id, suffix + "-other", now);
            var candidate = new AppUser
            {
                Username = $"{suffix}-candidate", DisplayName = "Phase5考生", PasswordHash = "self-check",
                MustChangePassword = false, Status = "Active", PrimaryDepartmentId = department.Id,
                CreatedAt = now, RowVersion = 1
            };
            tx.Users.Add(candidate);
            await tx.SaveChangesAsync();
            var candidateUser = new CheckUser(candidate.Id, candidate.Username, [], false, suffix);
            var candidateAttemptService = BuildAttemptService(provider, uow, candidateUser, BuildResultService(uow, candidateUser));
            var outsiderPermissions = new[] { Permissions.ExamAttemptRead, Permissions.ExamAttemptGrade,
                Permissions.ExamAttemptAdjustScore, Permissions.ExamResultPublish, Permissions.ReportRead, Permissions.ReportExport };
            var outsiderUser = new CheckUser(admin.Id, admin.Username, outsiderPermissions, false, suffix,
                [new(RoleCodes.DeptAdmin, otherDepartment.Id, "Department", outsiderPermissions)]);
            var outsiderReviews = BuildReviewService(uow, outsiderUser);
            var outsiderReports = BuildReportService(uow, outsiderUser, storage);
            var outsiderResults = BuildResultService(uow, outsiderUser);

            var bank = new QuestionBank
            {
                Name = $"Phase5题库-{suffix}", DepartmentId = department.Id, IsShared = false,
                Status = "Active", CreatedBy = admin.Id, CreatedAt = now, UpdatedAt = now, RowVersion = 1
            };
            tx.QuestionBanks.Add(bank);
            await tx.SaveChangesAsync();
            var question = new Question
            {
                BankId = bank.Id, Type = "SingleChoice", Status = "Draft", CreatedAt = now, UpdatedAt = now, RowVersion = 1
            };
            tx.Questions.Add(question);
            await tx.SaveChangesAsync();
            var version = new QuestionVersion
            {
                QuestionId = question.Id, VersionNo = 1, Stem = "Phase5客观题", AnswerJson = "{\"value\":\"A\"}",
                Difficulty = "Easy", DefaultScore = 5m, Status = "Draft", Source = "Manual",
                CreatedAt = now, RowVersion = 1
            };
            tx.QuestionVersions.Add(version);
            await tx.SaveChangesAsync();
            version.RowVersion++;
            tx.QuestionOptions.Add(new QuestionOption
            {
                QuestionVersionId = version.Id, Label = "A", Content = "正确", IsCorrect = true, SortOrder = 1
            });
            tx.QuestionOptions.Add(new QuestionOption
            {
                QuestionVersionId = version.Id, Label = "B", Content = "错误", IsCorrect = false, SortOrder = 2
            });
            await tx.SaveChangesAsync();
            version.Status = "Published";
            version.PublishedAt = now;
            version.PublishedBy = admin.Id;
            version.RowVersion++;
            await tx.SaveChangesAsync();
            question.CurrentVersionId = version.Id;
            question.Status = "Published";
            question.RowVersion++;
            await tx.SaveChangesAsync();

            var paper = new Paper
            {
                Title = $"Phase5试卷-{suffix}", DepartmentId = department.Id, Status = "Draft",
                CreatedBy = admin.Id, CreatedAt = now, UpdatedAt = now, RowVersion = 1
            };
            tx.Papers.Add(paper);
            await tx.SaveChangesAsync();
            var paperVersion = new PaperVersion
            {
                PaperId = paper.Id, VersionNo = 1, Title = paper.Title, TotalScore = 5m,
                DurationMinutes = 60, Status = "Draft",
                CreatedAt = now, RowVersion = 1
            };
            tx.PaperVersions.Add(paperVersion);
            await tx.SaveChangesAsync();
            var section = new PaperSection
            {
                PaperVersionId = paperVersion.Id, Title = "客观题", SortOrder = 1,
                SelectionMode = "Fixed", ScorePerQuestion = 5m
            };
            tx.PaperSections.Add(section);
            await tx.SaveChangesAsync();
            tx.PaperQuestions.Add(new PaperQuestion
            {
                PaperVersionId = paperVersion.Id, SectionId = section.Id,
                QuestionVersionId = version.Id, SortOrder = 1, Score = 5m
            });
            await tx.SaveChangesAsync();
            paperVersion.Status = "Published";
            paperVersion.PublishedAt = now;
            paperVersion.PublishedBy = admin.Id;
            paperVersion.RowVersion++;
            await tx.SaveChangesAsync();
            paper.CurrentVersionId = paperVersion.Id;
            paper.Status = "Published";
            paper.RowVersion++;
            await tx.SaveChangesAsync();

            var localNow = TimeFormat.ToLocal(now);
            var task = new ExamTask
            {
                DepartmentId = department.Id, Code = suffix, Title = "Phase5复核任务", Status = "Draft",
                PaperVersionId = paperVersion.Id, AvailableFrom = localNow.AddHours(-1), AvailableTo = localNow.AddDays(1),
                DurationMinutes = 60, AttemptLimit = 1, ScorePolicy = "Latest", RetakeApproval = "None",
                ResultReleasePolicy = "Manual", ResultStatus = "Unpublished", LateScorePolicy = "Score",
                CreatedBy = admin.Id, CreatedAt = now, RowVersion = 1
            };
            tx.ExamTasks.Add(task);
            await tx.SaveChangesAsync();
            task.Status = "Published";
            task.PublishedAt = now;
            task.RowVersion++;
            await tx.SaveChangesAsync();
            var assignment = new ExamAssignment
            {
                TaskId = task.Id, UserId = candidate.Id, DepartmentIdSnapshot = department.Id, Status = "Pending",
                AvailableFrom = task.AvailableFrom, AvailableTo = task.AvailableTo, AttemptsUsed = 0,
                CreatedAt = now, RowVersion = 1
            };
            tx.ExamAssignments.Add(assignment);
            await tx.SaveChangesAsync();
            assignment.Status = "Available";
            assignment.RowVersion++;
            await tx.SaveChangesAsync();
            assignment.Status = "InProgress";
            assignment.AttemptsUsed = 1;
            assignment.RowVersion++;
            await tx.SaveChangesAsync();
            var attempt = new ExamAttempt
            {
                AssignmentId = assignment.Id, AttemptNo = 1, Status = "InProgress",
                StartKey = $"start:{suffix}", PaperSnapshotJson = $"{{\"sections\":[{{\"questions\":[{{\"questionVersionId\":{version.Id},\"score\":5}}]}}]}}",
                StartedAt = now.AddMinutes(-10), DeadlineAt = now.AddMinutes(50), MaxScore = 5m, RowVersion = 1
            };
            tx.ExamAttempts.Add(attempt);
            await tx.SaveChangesAsync();
            var answer = new AttemptAnswer
            {
                // 模拟历史答卷中“有答案但被判为格式异常”的记录；当前写入守卫不会新造非法 JSON。
                AttemptId = attempt.Id, QuestionVersionId = version.Id, AnswerJson = "{\"value\":\"B\"}", Score = 0,
                MaxScore = 5m, GradingStatus = "Pending", CreatedAt = now, UpdatedAt = now, RowVersion = 1
            };
            tx.AttemptAnswers.Add(answer);
            await tx.SaveChangesAsync();
            attempt.Status = "Submitted";
            attempt.SubmittedAt = now.AddMinutes(-5);
            attempt.SubmitKey = $"submit:{suffix}";
            assignment.Status = "Submitted";
            assignment.RowVersion++;
            await tx.SaveChangesAsync();
            answer.GradingStatus = "Invalid";
            answer.UpdatedAt = now;
            answer.RowVersion++;
            await tx.SaveChangesAsync();
            attempt.Status = "Graded";
            attempt.AutoScore = 0m;
            attempt.Score = 0m;
            attempt.GradedAt = now;
            attempt.RowVersion++;
            assignment.Status = "Graded";
            assignment.RowVersion++;
            await tx.SaveChangesAsync();

            var queue = await adminReviews.GetQueueAsync(new() { TaskId = task.Id });
            Check(queue.TotalCount == 1 && queue.Items.Single().InvalidAnswerCount == 1,
                "objective format anomaly appears in the review queue");
            var scopedQueue = await outsiderReviews.GetQueueAsync(new() { TaskId = task.Id });
            Check(scopedQueue.TotalCount == 0, "department scope excludes another department's review queue");

            var reviewApi = Api(suffix, "review");
            var review = await adminReviews.AddReviewEventAsync(attempt.Id,
                new(ReviewTypes.Dispute, "考生对系统判分结果提出异议，请复核。"), reviewApi);
            Check(review.IsSuccess, "append objective review event");
            var reviewReplay = await adminReviews.AddReviewEventAsync(attempt.Id,
                new(ReviewTypes.Dispute, "考生对系统判分结果提出异议，请复核。"), reviewApi);
            Check(reviewReplay.IsSuccess && reviewReplay.Value == review.Value, "review event idempotency replay");
            CheckFailure(await outsiderReviews.AddReviewEventAsync(attempt.Id,
                new(ReviewTypes.Dispute, "越权范围不得写入复核事件。"), Api(suffix, "review-outsider")),
                "NOT_FOUND", "review event scope denial");
            Check(await tx.AttemptEvents.CountAsync(x => x.AttemptId == attempt.Id && x.EventType == ReviewTypes.Dispute) == 1,
                "review replay and scope denial leave exactly one event");
            var reviewEvents = await adminReviews.GetEventsAsync(attempt.Id);
            Check(reviewEvents.Count == 1 && reviewEvents[0].EventType == ReviewTypes.Dispute
                && reviewEvents[0].PayloadJson.Contains("reason", StringComparison.OrdinalIgnoreCase)
                && reviewEvents[0].ActorName == admin.DisplayName, "review detail exposes immutable reason, actor and time");
            Check(!(await outsiderResults.GetTasksAsync()).Any(x => x.Id == task.Id),
                "publication task options exclude out-of-scope tasks");
            Check((await adminResults.GetTasksAsync()).Any(x => x.Id == task.Id),
                "publication task options include visible tasks");
            var unchanged = await tx.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id);
            Check(unchanged.Status == "Graded" && unchanged.AutoScore == 0m && unchanged.Score == 0m,
                "review event does not change attempt state or scores");
            var unchangedAnswer = await tx.AttemptAnswers.AsNoTracking().SingleAsync(x => x.AttemptId == attempt.Id);
            Check(unchangedAnswer.GradingStatus == "Invalid" && unchangedAnswer.Score == 0m,
                "review event preserves per-question grading facts");

            CheckFailure(await adminReviews.AdjustScoreAsync(attempt.Id,
                new(0m, 4m, "原因长度不足", unchanged.RowVersion), Api(suffix, "adjust-short")),
                "REASON_REQUIRED", "adjustment reason guard");
            CheckFailure(await adminReviews.AdjustScoreAsync(attempt.Id,
                new(1m, 4m, "旧分数不一致时必须拒绝调分。", unchanged.RowVersion), Api(suffix, "adjust-old")),
                "OLD_SCORE_MISMATCH", "adjustment old-score guard");
            CheckFailure(await adminReviews.AdjustScoreAsync(attempt.Id,
                new(0m, 6m, "新分数不得超过本答卷满分。", unchanged.RowVersion), Api(suffix, "adjust-range")),
                "SCORE_OUT_OF_RANGE", "adjustment range guard");
            CheckFailure(await adminReviews.AdjustScoreAsync(attempt.Id,
                new(0m, 4m, "并发行版本不一致时必须拒绝。", unchanged.RowVersion + 1), Api(suffix, "adjust-version")),
                "ROW_VERSION_CONFLICT", "adjustment row-version guard");

            var adjustApi = Api(suffix, "adjust");
            var adjusted = await adminReviews.AdjustScoreAsync(attempt.Id,
                new(0m, 4m, "复核确认客观题格式异常，核准调整为四分。", unchanged.RowVersion), adjustApi);
            Check(adjusted is { IsSuccess: true } && adjusted.Value!.Score == 4m && adjusted.Value!.AutoScore == 0m,
                "adjustment changes only current score");
            var adjustReplay = await adminReviews.AdjustScoreAsync(attempt.Id,
                new(0m, 4m, "复核确认客观题格式异常，核准调整为四分。", unchanged.RowVersion), adjustApi);
            Check(adjustReplay is { IsSuccess: true } && adjusted is { IsSuccess: true }
                && adjustReplay.Value!.AdjustmentId == adjusted.Value!.AdjustmentId,
                "adjustment idempotency replay");
            CheckFailure(await outsiderReviews.AdjustScoreAsync(attempt.Id,
                new(4m, 3m, "越权范围不得写入调分记录。", adjusted is { IsSuccess: true } ? adjusted.Value!.RowVersion : -1), Api(suffix, "adjust-outsider")),
                "NOT_FOUND", "adjustment scope denial");
            var persisted = await tx.ExamAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id);
            Check(persisted.Score == 4m && persisted.AutoScore == 0m && persisted.RowVersion == unchanged.RowVersion + 1,
                "adjustment updates current score and row version once");
            Check(await tx.ScoreAdjustments.CountAsync(x => x.AttemptId == attempt.Id) == 1
                && await tx.AttemptEvents.CountAsync(x => x.AttemptId == attempt.Id && x.EventType == "ScoreAdjusted") == 1
                && await tx.AuditEvents.CountAsync(x => x.Action == "exam_attempt.adjust_score" && x.EntityId == attempt.Id) == 1
                && await tx.OutboxMessages.CountAsync(x => x.Type == "exam_attempt.score_adjusted" && x.AggregateId == attempt.Id) == 1,
                "adjustment, event, audit and outbox commit atomically once");
            var history = await adminReviews.GetAdjustmentsAsync(attempt.Id, 1, 20);
            Check(history.TotalCount == 1 && history.Items.Single().OldScore == 0m && history.Items.Single().NewScore == 4m,
                "immutable adjustment history is readable");

            var draftSummary = await adminReports.GetSummaryAsync(task.Id);
            Check(draftSummary is { IsSuccess: true } && draftSummary.Value!.Rows.Single().Values["assignedCount"] is 1
                && draftSummary.Value.Rows.Single().Values["completedCount"] is 1
                && draftSummary.Value.Rows.Single().Values["averageScore"] is null
                && draftSummary.Value.Rows.Single().Values["completionRate"] is 1m,
                "summary completion metrics exclude revoked assignments and hide unpublished scores");
            CheckFailure(await adminReports.GetDistributionAsync(task.Id), "RESULT_NOT_PUBLISHED",
                "unpublished distribution is hidden");
            CheckFailure(await outsiderReports.GetSummaryAsync(task.Id), "NOT_FOUND", "report task scope denial");
            var draftQuestions = await adminReports.GetTaskQuestionsAsync(task.Id);
            Check(draftQuestions is { IsSuccess: true } && draftQuestions.Value!.Rows.Single().Values["attemptCount"] is 1
                && draftQuestions.Value.Rows.Single().Values["unansweredCount"] is 0
                && draftQuestions.Value.Rows.Single().Values["invalidCount"] is 1
                && draftQuestions.Value.Rows.Single().Values["accuracy"] is 0m,
                "question accuracy treats invalid objective answer as incorrect");
            var draftAnomalies = await adminReports.GetAnomaliesAsync(task.Id, new());
            Check(draftAnomalies is { IsSuccess: true } && draftAnomalies.Value!.TotalCount == 3,
                "anomaly report combines invalid answer, review and adjustment events");

            var beforePublication = await candidateAttemptService.GetResultAsync(attempt.Id);
            Check(beforePublication is { IsPublished: false, Score: null }, "unpublished score is hidden from candidate");
            var publishApi = Api(suffix, "publish");
            var published = await adminResults.PublishAsync(task.Id, new(task.RowVersion), publishApi);
            Check(published is { IsSuccess: true } && published.Value!.ReleaseNo == 1, "publish immutable result snapshot");
            var publishReplay = await adminResults.PublishAsync(task.Id, new(task.RowVersion), publishApi);
            Check(publishReplay is { IsSuccess: true } && publishReplay.Value!.ReleaseId == published.Value!.ReleaseId,
                "publication idempotency replay");
            CheckFailure(await adminResults.PublishAsync(task.Id, new(task.RowVersion), Api(suffix, "publish-again")),
                "RESULT_ALREADY_PUBLISHED", "duplicate publication guard");
            var snapshot = await tx.ResultReleaseItems.AsNoTracking().SingleAsync(x => x.ReleaseId == published.Value!.ReleaseId);
            Check(snapshot.FinalScore == 4m && snapshot.MaxScore == 5m && snapshot.AttemptId == attempt.Id,
                "snapshot stores adjusted score and source attempt");
            var source = System.Text.Json.JsonDocument.Parse(snapshot.SourceJson);
            Check(source.RootElement.GetProperty("attempts")[0].GetProperty("autoScore").GetDecimal() == 0m
                && source.RootElement.GetProperty("attempts")[0].GetProperty("currentScore").GetDecimal() == 4m,
                "snapshot preserves automatic and adjusted scoring facts");
            var candidateResult = await candidateAttemptService.GetResultAsync(attempt.Id);
            Check(candidateResult is { IsPublished: true, Score: 4m, ReleaseNo: 1, FinalAttemptId: not null, FinalAttemptNo: 1 }
                && candidateResult.FinalAttemptId == attempt.Id,
                "candidate reads published snapshot and final-score source");
            var immutable = await tx.ResultReleases.AsNoTracking().SingleAsync(x => x.Id == published.Value!.ReleaseId);
            Check(immutable.PayloadSha256.Length == 64 && immutable.SummaryJson.Contains("scoredCount"),
                "publication stores deterministic hash and summary");
            Check(await adminResults.GetHistoryAsync(task.Id) is { Count: 1 } releaseHistory && releaseHistory[0].ScoredCount == 1,
                "release history is readable");
            CheckFailure(await adminReviews.AddReviewEventAsync(attempt.Id,
                new(ReviewTypes.VoidRequest, "成绩发布后不得再追加作废申请。"), Api(suffix, "review-published")),
                "RESULT_ALREADY_PUBLISHED", "published result freezes review events");
            CheckFailure(await adminReviews.AdjustScoreAsync(attempt.Id,
                new(4m, 3m, "成绩发布后不得再次直接调分。", persisted.RowVersion), Api(suffix, "adjust-published")),
                "RESULT_ALREADY_PUBLISHED", "published result freezes adjustments");
            CheckFailure(await adminAttempts.VoidAsync(attempt.Id, new(persisted.RowVersion, "成绩发布后不得直接作废答卷。"), Api(suffix, "void-published")),
                "RESULT_ALREADY_PUBLISHED", "published result freezes voiding");
            CheckFailure(await candidateAttemptService.StartAsync(assignment.Id, new(assignment.RowVersion), Api(suffix, "start-published")),
                "RESULT_ALREADY_PUBLISHED", "published result blocks starting or resuming an exam");
            CheckFailure(await adminResults.WithdrawAsync(task.Id, new(published.Value!.RowVersion, "原因长度不足"), Api(suffix, "withdraw-short")),
                "REASON_REQUIRED", "withdrawal reason guard");
            var withdrawApi = Api(suffix, "withdraw");
            var withdrawn = await adminResults.WithdrawAsync(task.Id,
                new(published.Value!.RowVersion, "成绩公示后发现需要复核，请暂时撤回。"), withdrawApi);
            Check(withdrawn is { IsSuccess: true }, "withdraw publication without deleting snapshot");
            var withdrawnReplay = await adminResults.WithdrawAsync(task.Id,
                new(published.Value!.RowVersion, "成绩公示后发现需要复核，请暂时撤回。"), withdrawApi);
            Check(withdrawnReplay is { IsSuccess: true } && withdrawnReplay.Value!.ReleaseId == published.Value!.ReleaseId,
                "withdrawal idempotency replay");
            Check((await candidateAttemptService.GetResultAsync(attempt.Id)) is { IsPublished: false, Score: null },
                "withdrawn score is immediately hidden from candidate");
            Check(await tx.ResultReleaseItems.AsNoTracking().AnyAsync(x => x.ReleaseId == published.Value!.ReleaseId && x.FinalScore == 4m),
                "withdrawal preserves immutable snapshot");
            var afterWithdraw = await adminReviews.AdjustScoreAsync(attempt.Id,
                new(4m, 3m, "撤回后复核确认最终成绩调整为三分。", persisted.RowVersion), Api(suffix, "adjust-after-withdraw"));
            Check(afterWithdraw is { IsSuccess: true }, "withdrawal unlocks score adjustment");
            var republished = await adminResults.PublishAsync(task.Id, new(withdrawn.Value!.RowVersion), Api(suffix, "republish"));
            Check(republished is { IsSuccess: true } && republished.Value!.ReleaseNo == 2
                && republished.Value!.ReleaseId != published.Value!.ReleaseId, "republish creates a new release");
            Check((await tx.ResultReleaseItems.AsNoTracking().SingleAsync(x => x.ReleaseId == republished.Value!.ReleaseId)).FinalScore == 3m
                && (await tx.ResultReleaseItems.AsNoTracking().SingleAsync(x => x.ReleaseId == published.Value!.ReleaseId)).FinalScore == 4m,
                "new release does not rewrite old snapshot");
            Check((await candidateAttemptService.GetResultAsync(attempt.Id)) is { Score: 3m, ReleaseNo: 2 },
                "candidate reads latest republished snapshot");
            Check((await adminResults.GetHistoryAsync(task.Id)).Count == 2, "all releases remain traceable");
            Check((await adminReports.GetTasksAsync()).Single().Id == task.Id,
                "report task options are permission- and scope-filtered");
            var resultDetail = await adminReports.GetResultsAsync(task.Id);
            Check(resultDetail is { IsSuccess: true } && resultDetail.Value!.Rows.Count == 1
                && resultDetail.Value.Rows.Single().Values["finalScore"] is 3m,
                "result detail page reads current published snapshot");

            var summary = await adminReports.GetSummaryAsync(task.Id);
            Check(summary is { IsSuccess: true } && summary.Value!.Rows.Single().Values["averageScore"] is 3m
                && summary.Value.Rows.Single().Values["highestScore"] is 3m
                && summary.Value.Rows.Single().Values["lowestScore"] is 3m
                && summary.Value.Rows.Single().Values["releaseNo"] is 2,
                "published summary reads current immutable snapshot");
            var distribution = await adminReports.GetDistributionAsync(task.Id);
            Check(distribution is { IsSuccess: true } && distribution.Value!.Rows.Single(x => Equals(x.Values["band"], "60–69.99%")).Values["count"] is 1,
                "distribution uses final score percentage");
            var passRate = await adminReports.GetPassRateAsync(task.Id);
            Check(passRate is { IsSuccess: true } && passRate.Value!.Rows.Single().Values["passRate"] is null,
                "missing pass score does not fabricate pass rate");
            var compare = await adminReports.GetDepartmentCompareAsync(new(task.Id, null, null));
            Check(compare is { IsSuccess: true } && compare.Value!.Rows.Single().Values["scoredCount"] is 1
                && compare.Value.Rows.Single().Values["averageScore"] is 3m,
                "department comparison uses published snapshot");

            var unansweredUser = new AppUser
            {
                Username = $"{suffix}-unanswered", DisplayName = "Phase5未答考生", PasswordHash = "self-check",
                MustChangePassword = false, Status = "Active", PrimaryDepartmentId = department.Id,
                CreatedAt = now, RowVersion = 1
            };
            tx.Users.Add(unansweredUser);
            await tx.SaveChangesAsync();
            var unansweredAssignment = new ExamAssignment
            {
                TaskId = task.Id, UserId = unansweredUser.Id, DepartmentIdSnapshot = department.Id, Status = "Pending",
                AvailableFrom = task.AvailableFrom, AvailableTo = task.AvailableTo, AttemptsUsed = 1,
                CreatedAt = now, RowVersion = 1
            };
            tx.ExamAssignments.Add(unansweredAssignment);
            await tx.SaveChangesAsync();
            unansweredAssignment.Status = "Available";
            unansweredAssignment.RowVersion++;
            await tx.SaveChangesAsync();
            unansweredAssignment.Status = "InProgress";
            unansweredAssignment.RowVersion++;
            await tx.SaveChangesAsync();
            var unansweredAttempt = new ExamAttempt
            {
                AssignmentId = unansweredAssignment.Id, AttemptNo = 1, Status = "InProgress",
                StartKey = $"start:{suffix}-unanswered", PaperSnapshotJson = attempt.PaperSnapshotJson,
                StartedAt = now.AddMinutes(-20), DeadlineAt = now.AddMinutes(40), MaxScore = 5m, RowVersion = 1
            };
            tx.ExamAttempts.Add(unansweredAttempt);
            await tx.SaveChangesAsync();
            var unansweredAnswer = new AttemptAnswer
            {
                AttemptId = unansweredAttempt.Id, QuestionVersionId = version.Id, AnswerJson = null,
                Score = 0m, MaxScore = 5m, GradingStatus = "Pending", CreatedAt = now, UpdatedAt = now, RowVersion = 1
            };
            tx.AttemptAnswers.Add(unansweredAnswer);
            await tx.SaveChangesAsync();
            unansweredAttempt.Status = "Submitted";
            unansweredAttempt.SubmittedAt = now.AddMinutes(-10);
            unansweredAttempt.SubmitKey = $"submit:{suffix}-unanswered";
            unansweredAttempt.RowVersion++;
            unansweredAssignment.Status = "Submitted";
            unansweredAssignment.RowVersion++;
            await tx.SaveChangesAsync();
            unansweredAnswer.GradingStatus = "Graded";
            unansweredAnswer.UpdatedAt = now;
            unansweredAnswer.RowVersion++;
            unansweredAttempt.Status = "Graded";
            unansweredAttempt.GradedAt = now;
            unansweredAttempt.AutoScore = 0m;
            unansweredAttempt.Score = 0m;
            unansweredAttempt.RowVersion++;
            unansweredAssignment.Status = "Graded";
            unansweredAssignment.RowVersion++;
            await tx.SaveChangesAsync();
            var questions = await adminReports.GetTaskQuestionsAsync(task.Id);
            Check(questions is { IsSuccess: true } && questions.Value!.Rows.Single().Values["attemptCount"] is 2
                && questions.Value.Rows.Single().Values["answeredCount"] is 1
                && questions.Value.Rows.Single().Values["unansweredCount"] is 1
                && questions.Value.Rows.Single().Values["correctCount"] is 0,
                $"whole-question non-response remains in accuracy denominator: {string.Join(",", questions.Value?.Rows.Single().Values.Select(x => $"{x.Key}={x.Value}") ?? [])}");
            await CheckExportsAsync(uow, tx, adminUser, adminReports, outsiderReports, task.Id, version.Id, storage, suffix);
            var reportWithdraw = await adminResults.WithdrawAsync(task.Id,
                new(republished.Value!.RowVersion, "报表验收完成后撤回成绩以验证隐藏。"), Api(suffix, "withdraw-report"));
            Check(reportWithdraw is { IsSuccess: true }, "withdraw published result after report checks");
            CheckFailure(await adminReports.GetDistributionAsync(task.Id), "RESULT_NOT_PUBLISHED",
                "withdrawn distribution is hidden");
            CheckFailure(await adminReports.ExportAsync(task.Id, new(ReportTypes.Results, "CSV"), Api(suffix, "export-withdrawn")),
                "RESULT_NOT_PUBLISHED", "withdrawn result export is refused");
            var hiddenSummary = await adminReports.GetSummaryAsync(task.Id);
            Check(hiddenSummary is { IsSuccess: true } && hiddenSummary.Value!.Rows.Single().Values["averageScore"] is null,
                "withdrawn summary hides score metrics");
        }, commit: false);
        }
        finally
        {
            if (Directory.Exists(reportRoot)) Directory.Delete(reportRoot, recursive: true);
        }
        Check(!await db.ExamTasks.AsNoTracking().AnyAsync(x => x.Code == suffix)
            && !await db.Users.AsNoTracking().AnyAsync(x => x.Username == $"{suffix}-candidate"),
            "Phase 5 fixtures are fully rolled back");
        logger.LogInformation("Phase 5 review/scoring self-check passed; all fixtures rolled back.");
    }

    private ExamReviewService BuildReviewService(IUnitOfWork uow, ICurrentUser user)
    {
        var audit = new AuditRepository();
        var outbox = new OutboxRepository();
        return new(uow, new ExamReviewRepository(), new AttemptRepository(clock),
            new ExamTaskRepository(), audit, outbox, new ExamIdempotencyService(user, audit),
            new DataScopeService(user, new DepartmentRepository(uow), uow), user, clock);
    }

    private ExamAttemptService BuildAttemptService(IServiceProvider provider, IUnitOfWork uow, ICurrentUser user,
        ResultReleaseService results)
    {
        var audit = new AuditRepository();
        return new(uow, new AttemptRepository(clock), new AssignmentRepository(clock),
            new ExamTaskRepository(), audit, new OutboxRepository(), new ExamIdempotencyService(user, audit),
            new DataScopeService(user, new DepartmentRepository(uow), uow), user, clock, results,
            NullLogger<ExamAttemptService>.Instance);
    }

    private ReportService BuildReportService(IUnitOfWork uow, ICurrentUser user, IFileStorage storage,
        TimeSpan? exportRetention = null)
    {
        var audit = new AuditRepository();
        return new(uow, new ReportRepository(), audit, new OutboxRepository(), new ExamIdempotencyService(user, audit),
            new DataScopeService(user, new DepartmentRepository(uow), uow), user, clock, storage,
            exportRetention ?? TimeSpan.FromHours(24), NullLogger<ReportService>.Instance);
    }

    private static void RunReportFileCheck()
    {
        var table = new ReportTable("安全导出", DateTime.UnixEpoch,
            [new("text", "文本"), new("number", "数值", ReportColumnKind.Decimal), new("percent", "比率 (%)", ReportColumnKind.Percent)],
            [new ReportRow(new Dictionary<string, object?> { ["text"] = "=1+1", ["number"] = 3.5m, ["percent"] = 0.75m })]);
        var csv = System.Text.Encoding.UTF8.GetString(ReportFileWriter.WriteCsv(table));
        Check(csv.Contains("'=1+1", StringComparison.Ordinal) && csv.Contains("3.5", StringComparison.Ordinal)
            && csv.Contains("75.00", StringComparison.Ordinal), "CSV neutralizes formula injection and preserves numeric display");
        var xlsx = ReportFileWriter.WriteXlsx(table);
        using var archive = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        Check(archive.GetEntry("xl/worksheets/sheet1.xml") is not null
            && archive.GetEntry("xl/workbook.xml") is not null, "XLSX contains workbook and worksheet parts");
        using var reader = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = reader.ReadToEnd();
        Check(XDocument.Parse(sheet).Root?.Name.LocalName == "worksheet"
            && !sheet.Contains("<f>", StringComparison.Ordinal) && !sheet.Contains("<f ", StringComparison.Ordinal),
            "XLSX is readable XML and contains no formulas");
    }

    private async Task CheckExportsAsync(IUnitOfWork uow, ExamDbContext tx, ICurrentUser adminUser, ReportService reports,
        ReportService outsider, long taskId, long questionVersionId, IFileStorage storage, string suffix)
    {
        try
        {
            var exportApi = Api(suffix, "report-export");
            var exported = await reports.ExportAsync(taskId, new(ReportTypes.Summary, "CSV"), exportApi);
            Check(exported is { IsSuccess: true } && exported.Value!.RowCount == 1, "scoped CSV report export");
            var exportOutput = exported.Value!;
            var replay = await reports.ExportAsync(taskId, new(ReportTypes.Summary, "CSV"), exportApi);
            Check(replay is { IsSuccess: true } && replay.Value!.ExportId == exportOutput.ExportId,
                "report export idempotency replay");
            CheckFailure(await reports.ExportAsync(taskId, new(ReportTypes.Results, "CSV"), exportApi with { RequestHash = "different" }),
                "IDEMPOTENCY_CONFLICT", "report export request hash guard");
            Check(await tx.ReportExports.CountAsync(x => x.TaskId == taskId) == 1
                && await tx.AuditEvents.CountAsync(x => x.Action == "report.export" && x.EntityId == exportOutput.ExportId) == 1
                && await tx.OutboxMessages.CountAsync(x => x.Type == "report.exported" && x.AggregateId == exportOutput.ExportId) == 1,
                "export record, audit and outbox are atomic and idempotent");
            CheckFailure(await outsider.ExportAsync(taskId, new(ReportTypes.Summary, "CSV"), Api(suffix, "report-export-outsider")),
                "NOT_FOUND", "report export scope denial");
            CheckFailure(await outsider.DownloadAsync(exportOutput.ExportId), "NOT_FOUND", "report download scope denial");
            CheckFailure(await reports.ExportAsync(taskId, new("Unknown", "CSV"), Api(suffix, "report-type")),
                "VALIDATION_ERROR", "report type whitelist");
            CheckFailure(await reports.ExportAsync(taskId, new(ReportTypes.Summary, "PDF"), Api(suffix, "report-format")),
                "VALIDATION_ERROR", "report format whitelist");
            Check((await reports.ExportAsync(taskId, new(ReportTypes.Results, "CSV"), Api(suffix, "report-results"))).IsSuccess,
                "published result detail export");
            Check((await reports.ExportAsync(taskId, new(ReportTypes.DepartmentCompare, "CSV"), Api(suffix, "report-departments"))).IsSuccess,
                "department comparison export");
            Check((await reports.ExportAsync(taskId, new(ReportTypes.Anomalies, "CSV"), Api(suffix, "report-anomalies"))).IsSuccess,
                "anomaly export");

            var xlsxExport = await reports.ExportAsync(taskId, new(ReportTypes.Questions, "XLSX"), Api(suffix, "report-xlsx"));
            Check(xlsxExport is { IsSuccess: true }, "question report XLSX export");
            var xlsxOutput = xlsxExport.Value!;
            var xlsxEntity = await tx.ReportExports.AsNoTracking().SingleAsync(x => x.Id == xlsxOutput.ExportId);
            Check(xlsxEntity.FileKey.StartsWith("reports/", StringComparison.Ordinal)
                && !Path.IsPathRooted(xlsxEntity.FileKey), "export database key is a relative report path");
            var xlsxDownload = await reports.DownloadAsync(xlsxOutput.ExportId);
            Check(xlsxDownload.IsSuccess, "authorized XLSX download");
            using (xlsxDownload.Value!.Content)
            {
                using var memory = new MemoryStream();
                await xlsxDownload.Value.Content.CopyToAsync(memory);
                using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true);
                memory.Position = 0;
                Check(archive.GetEntry("xl/worksheets/sheet1.xml") is not null, "downloaded XLSX remains a valid ZIP package");
            }

            var csvDownload = await reports.DownloadAsync(exportOutput.ExportId);
            Check(csvDownload.IsSuccess, "authorized CSV download");
            var csvOutput = csvDownload.Value!;
            using (csvOutput.Content)
            using (var reader = new StreamReader(csvOutput.Content))
                Check((await reader.ReadToEndAsync()).Contains("任务名称", StringComparison.Ordinal), "downloaded CSV contains report content");
            Check(await tx.AuditEvents.CountAsync(x => x.Action == "report.export_download"
                && x.EntityId == exportOutput.ExportId) == 1, "download is independently audited");
            var expiringReports = BuildReportService(uow, adminUser, storage, TimeSpan.FromMinutes(-1));
            var expiringExport = await expiringReports.ExportAsync(taskId, new(ReportTypes.Summary, "CSV"), Api(suffix, "report-expired"));
            Check(expiringExport.IsSuccess, "create already-expired export without mutating append-only facts");
            CheckFailure(await reports.DownloadAsync(expiringExport.Value!.ExportId), "NOT_FOUND", "expired export download is refused");
            Check((await reports.GetQuestionAccuracyAsync(questionVersionId, taskId)).IsSuccess,
                "single question accuracy endpoint");
        }
        finally
        {
            foreach (var entity in await tx.ReportExports.AsNoTracking().Where(x => x.TaskId == taskId).ToListAsync())
                storage.DeleteExport(entity.FileKey);
        }
    }

    private ResultReleaseService BuildResultService(IUnitOfWork uow, ICurrentUser user)
    {
        var audit = new AuditRepository();
        return new(uow, new ExamTaskRepository(), audit, new OutboxRepository(), new ExamIdempotencyService(user, audit),
            new DataScopeService(user, new DepartmentRepository(uow), uow), user, clock,
            NullLogger<ResultReleaseService>.Instance);
    }

    private static void RunFinalScoreCheck()
    {
        var attempts = new[]
        {
            new ScoredAttempt(11, 1, 2m, 5m, 2m, DateTime.UnixEpoch),
            new ScoredAttempt(12, 2, 4m, 5m, 3m, DateTime.UnixEpoch),
            new ScoredAttempt(13, 3, 4m, 5m, 1m, DateTime.UnixEpoch)
        };
        Check(ExamAlgorithms.CalculateFinalScore("Latest", 4m, attempts) is { AttemptId: 13, FinalScore: 4m, Passed: true },
            "Latest final-score policy");
        Check(ExamAlgorithms.CalculateFinalScore("First", 4m, attempts) is { AttemptId: 11, FinalScore: 2m, Passed: false },
            "First final-score policy");
        Check(ExamAlgorithms.CalculateFinalScore("Highest", 4m, attempts) is { AttemptId: 13, FinalScore: 4m },
            "Highest final-score tie break");
        Check(ExamAlgorithms.CalculateFinalScore("Average", 4m, attempts) is { FinalScore: 3.33m, Passed: false },
            "Average final-score rounding");
        Check(ExamAlgorithms.CalculateFinalScore("Latest", 4m, []) is { FinalScore: null, MaxScore: null, AttemptId: null },
            "missing valid attempt does not fabricate zero");
    }

    private static async Task<Department> CreateDepartmentAsync(ExamDbContext db, long parentId, string suffix, DateTime now)
    {
        var root = await db.Departments.AsNoTracking().SingleAsync(x => x.Id == parentId);
        var segment = Random.Shared.Next(100, 1000).ToString("000");
        while (await db.Departments.AnyAsync(x => x.ParentId == parentId && x.PathSegment == segment))
            segment = Random.Shared.Next(100, 1000).ToString("000");
        var department = new Department
        {
            ParentId = parentId, Name = $"Phase5范围-{suffix}", Path = root.Path + segment + ".",
            PathSegment = segment, SortOrder = 0, IsActive = true, CreatedAt = now, RowVersion = 1
        };
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        return department;
    }

    private static ApiAuditMetadata Api(string suffix, string name)
        => new($"p5-{name}-{Guid.NewGuid():N}", $"hash-{name}-{suffix}", "POST", $"/api/phase5-check/{name}");

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Phase 5 self-check failed: {name}");
    }

    private static void CheckFailure(Result result, string code, string name)
        => Check(!result.IsSuccess && result.Code == code, name);

    private sealed class CheckUser(long userId, string username, IReadOnlyList<string> permissions,
        bool administrator, string trace, IReadOnlyList<RoleAssignmentData>? roles = null) : ICurrentUser
    {
        private readonly IReadOnlyList<RoleAssignmentData> _roles = roles ?? [];
        public long? Id => userId;
        public string? DisplayName => username;
        public string? Username => username;
        public bool IsAuthenticated => true;
        public bool MustChangePassword => false;
        public IReadOnlyList<RoleAssignmentData> Roles => administrator
            ? [new(RoleCodes.PlatformAdmin, null, "All", permissions)] : _roles;
        public IReadOnlyList<DepartmentAccessData> Departments => [];
        public Task LoadAsync() => Task.CompletedTask;
        public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
        public bool HasAnyPermission(params string[] permissions) => permissions.Any(HasPermission);
        public bool HasRole(string roleCode) => Roles.Any(x => x.Code == roleCode);
        public AuditContext GetAuditContext() => new(userId, administrator ? RoleCodes.PlatformAdmin : RoleCodes.Candidate,
            trace, "127.0.0.1", "ExamOps.Phase5Check");
    }
}
