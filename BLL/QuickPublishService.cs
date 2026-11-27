using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.BLL;

public sealed class QuickPublishService(
    IUnitOfWork uow,
    PaperService papers,
    ExamTaskService tasks,
    AssignmentService assignments,
    QuestionService questions,
    AuditRepository audit,
    ExamIdempotencyService idempotency,
    DataScopeService scope,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<IReadOnlyList<QuickBankSummary>> GetAvailableBanksAsync()
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.QuestionRead))
            return [];

        var banks = await questions.GetBanksAsync();
        if (banks.Count == 0) return [];

        var bankIds = banks.Select(b => b.Id).ToArray();
        var stats = await uow.Db.Questions.AsNoTracking()
            .Where(q => bankIds.Contains(q.BankId) && q.Status != "Archived")
            .GroupBy(q => new { q.BankId, q.Status, q.Type })
            .Select(g => new { g.Key.BankId, g.Key.Status, g.Key.Type, Count = g.Count() })
            .ToListAsync();

        return banks.Select(b => new QuickBankSummary(
            b.Id,
            b.Name,
            b.DepartmentId,
            b.DepartmentName,
            stats.Where(x => x.BankId == b.Id && x.Status == "Published" && x.Type == "SingleChoice").Sum(x => x.Count),
            stats.Where(x => x.BankId == b.Id && x.Status == "Published" && x.Type == "MultipleChoice").Sum(x => x.Count),
            stats.Where(x => x.BankId == b.Id && x.Status == "Published" && x.Type == "TrueFalse").Sum(x => x.Count),
            stats.Where(x => x.BankId == b.Id && x.Status == "Draft").Sum(x => x.Count)
        )).ToList();
    }

    public async Task<Result<int>> PublishDraftQuestionsInBankAsync(long bankId)
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.QuestionPublish))
            return Result<int>.Fail("无权发布题目", "PERMISSION_DENIED");

        var drafts = await (from q in uow.Db.Questions.AsNoTracking()
                            join v in uow.Db.QuestionVersions.AsNoTracking() on q.Id equals v.QuestionId
                            where q.BankId == bankId && q.Status == "Draft" && v.Status == "Draft"
                            select new { q.Id, VersionId = v.Id, q.RowVersion })
            .ToListAsync();

        var published = 0;
        foreach (var d in drafts.GroupBy(x => x.Id).Select(g => g.OrderByDescending(x => x.VersionId).First()))
        {
            var r = await questions.PublishAsync(d.Id, d.VersionId, d.RowVersion);
            if (r.IsSuccess) published++;
        }
        return Result<int>.Ok(published);
    }

    public async Task<Result<QuickPublishResult>> ExecuteAsync(QuickPublishRequest req)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated)
            return Result<QuickPublishResult>.Fail("请先登录", "UNAUTHORIZED");

        if (!currentUser.HasPermission(Permissions.PaperCreate)
            || !currentUser.HasPermission(Permissions.PaperUpdate)
            || !currentUser.HasPermission(Permissions.PaperPublish)
            || !currentUser.HasPermission(Permissions.ExamTaskCreate)
            || !currentUser.HasPermission(Permissions.ExamTaskPublish))
            return Result<QuickPublishResult>.Fail("当前账号缺少组卷或发布考试任务权限", "PERMISSION_DENIED");

        if (req.AssignWholeDepartment && !currentUser.HasPermission(Permissions.ExamAssignmentCreate))
            return Result<QuickPublishResult>.Fail("当前账号缺少下发考试安排权限", "PERMISSION_DENIED");

        var title = req.ExamTitle?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            return Result<QuickPublishResult>.Fail("考试名称需为 1–200 个字符", "INVALID_TITLE");
        if (req.DepartmentId <= 0 || req.BankId <= 0)
            return Result<QuickPublishResult>.Fail("请选择所属部门与题库", "VALIDATION_ERROR");
        if (req.AvailableTo <= req.AvailableFrom)
            return Result<QuickPublishResult>.Fail("结束时间必须晚于开放时间", "INVALID_WINDOW");
        if (req.DurationMinutes is < 1 or > 600)
            return Result<QuickPublishResult>.Fail("考试时长需为 1–600 分钟", "INVALID_DURATION");
        if (req.AttemptLimit < 1)
            return Result<QuickPublishResult>.Fail("允许考试次数至少为 1", "INVALID_ATTEMPTS");

        if (!(await scope.EnsureDepartmentAccessAsync(req.DepartmentId, Permissions.ExamTaskCreate)).IsSuccess)
            return Result<QuickPublishResult>.Fail("无权在该部门下发布考试", "OUT_OF_SCOPE");

        var isRandom = string.Equals(req.SelectionMode, "RandomByType", StringComparison.Ordinal);
        var typeSpecs = new List<(string Type, string Label, int Count, decimal Score)>();
        if (req.IncludeSingle && req.SingleCount > 0) typeSpecs.Add(("SingleChoice", "单选题", req.SingleCount, req.SingleScore));
        if (req.IncludeMultiple && req.MultipleCount > 0) typeSpecs.Add(("MultipleChoice", "多选题", req.MultipleCount, req.MultipleScore));
        if (req.IncludeTrueFalse && req.TrueFalseCount > 0) typeSpecs.Add(("TrueFalse", "判断题", req.TrueFalseCount, req.TrueFalseScore));
        if (typeSpecs.Count == 0)
            return Result<QuickPublishResult>.Fail("请至少勾选一种题型且题量大于 0", "NO_QUESTIONS_SELECTED");

        var sectionInputs = new List<PaperSectionInput>();
        var totalQuestions = 0;
        decimal totalScore = 0;
        var sortOrder = 1;

        foreach (var spec in typeSpecs)
        {
            if (spec.Score is <= 0 or > 9999.99m)
                return Result<QuickPublishResult>.Fail($"{spec.Label}每题分值需大于 0", "INVALID_SCORE");

            if (isRandom)
            {
                sectionInputs.Add(new PaperSectionInput(
                    -sortOrder, spec.Label, sortOrder, "Random",
                    spec.Count, spec.Score, req.BankId, null, spec.Type, null, []));
                totalQuestions += spec.Count;
                totalScore += spec.Count * spec.Score;
            }
            else
            {
                var candidates = await papers.GetCandidatesAsync(new PaperCandidateQuery(req.BankId, null, spec.Type, null, null, 1, spec.Count));
                if (candidates.Items.Count < spec.Count)
                    return Result<QuickPublishResult>.Fail($"{spec.Label}已发布题目不足（需要 {spec.Count} 题，当前仅 {candidates.Items.Count} 题）", "INSUFFICIENT_QUESTIONS");

                var qIds = candidates.Items.Take(spec.Count).Select(x => x.QuestionVersionId).ToArray();
                sectionInputs.Add(new PaperSectionInput(
                    -sortOrder, spec.Label, sortOrder, "Fixed",
                    null, spec.Score, null, null, null, null, qIds));
                totalQuestions += qIds.Length;
                totalScore += qIds.Length * spec.Score;
            }
            sortOrder++;
        }

        if (req.PassScore is < 0 || (req.PassScore.HasValue && req.PassScore.Value > totalScore))
            return Result<QuickPublishResult>.Fail($"及格分需在 0–{totalScore:0.##} 之间", "INVALID_PASS_SCORE");

        Result<QuickPublishResult>? outcome = null;
        var api = BuildIdempotencyMetadata(req, title);

        // 单一外层事务编排：幂等锁 + 建卷 → 配置小节/随机池 → 发布试卷 → 建任务 → 发布任务 → (可选)下发考生；任一步失败全量回滚。
        try
        {
        await uow.ExecuteInTransactionAsync(async db =>
        {
            var replay = await idempotency.FindReplayAsync<QuickPublishResult>(db, api);
            if (replay is not null)
            {
                outcome = Result<QuickPublishResult>.Ok(replay);
                return;
            }

            var createdPaper = await papers.CreateAsync(new CreatePaperInput($"{title} · 试卷", req.DepartmentId, req.DurationMinutes));
            if (!createdPaper.IsSuccess || createdPaper.Value is null)
            {
                outcome = Result<QuickPublishResult>.Fail(createdPaper.Error ?? "创建试卷失败", createdPaper.Code);
                throw new QuickPublishRollbackException();
            }

            var paperDetail = await papers.GetDetailAsync(createdPaper.Value.PaperId, createdPaper.Value.VersionId);
            if (paperDetail is null)
            {
                outcome = Result<QuickPublishResult>.Fail("读取新建试卷失败", "NOT_FOUND");
                throw new QuickPublishRollbackException();
            }

            var updatedPaper = await papers.UpdateDraftAsync(
                paperDetail.Id, paperDetail.VersionId, paperDetail.RowVersion, paperDetail.VersionRowVersion,
                new PaperVersionInput(paperDetail.Title, req.DurationMinutes, sectionInputs));
            if (!updatedPaper.IsSuccess)
            {
                outcome = Result<QuickPublishResult>.Fail(updatedPaper.Error ?? "保存试卷结构失败", updatedPaper.Code);
                throw new QuickPublishRollbackException();
            }

            paperDetail = await papers.GetDetailAsync(paperDetail.Id, paperDetail.VersionId);
            if (paperDetail is null)
            {
                outcome = Result<QuickPublishResult>.Fail("读取试卷结构失败", "NOT_FOUND");
                throw new QuickPublishRollbackException();
            }

            if (isRandom)
            {
                foreach (var sec in paperDetail.Sections.Where(s => s.SelectionMode == "Random"))
                {
                    var poolRes = await papers.ApplyBlueprintAsync(
                        paperDetail.Id, paperDetail.VersionId, sec.Id, paperDetail.RowVersion, paperDetail.VersionRowVersion);
                    if (!poolRes.IsSuccess)
                    {
                        outcome = Result<QuickPublishResult>.Fail(poolRes.Error ?? "生成随机候选题池失败", poolRes.Code);
                        throw new QuickPublishRollbackException();
                    }
                    paperDetail = await papers.GetDetailAsync(paperDetail.Id, paperDetail.VersionId);
                    if (paperDetail is null)
                    {
                        outcome = Result<QuickPublishResult>.Fail("读取更新后的试卷失败", "NOT_FOUND");
                        throw new QuickPublishRollbackException();
                    }
                }
            }

            var publishedPaper = await papers.PublishAsync(
                paperDetail.Id,
                new PaperPublishInput(paperDetail.VersionId, paperDetail.VersionRowVersion),
                paperDetail.RowVersion);
            if (!publishedPaper.IsSuccess)
            {
                outcome = Result<QuickPublishResult>.Fail(publishedPaper.Error ?? "发布试卷失败", publishedPaper.Code);
                throw new QuickPublishRollbackException();
            }

            var localNow = TimeFormat.ToLocal(clock.UtcNow);
            var taskCode = $"QP-{localNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
            var createTask = await tasks.CreateAsync(new CreateExamTaskInput(
                taskCode,
                title,
                string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                req.DepartmentId,
                DateOnly.FromDateTime(req.AvailableFrom),
                paperDetail.VersionId,
                DateTime.SpecifyKind(req.AvailableFrom, DateTimeKind.Unspecified),
                DateTime.SpecifyKind(req.AvailableTo, DateTimeKind.Unspecified),
                req.DurationMinutes,
                req.AttemptLimit,
                "Latest",
                "None",
                0,
                req.PassScore,
                req.ResultReleasePolicy,
                "Score"));
            if (!createTask.IsSuccess || createTask.Value is null)
            {
                outcome = Result<QuickPublishResult>.Fail(createTask.Error ?? "创建考试任务失败", createTask.Code);
                throw new QuickPublishRollbackException();
            }

            var taskDetail = await tasks.GetDetailAsync(createTask.Value.TaskId);
            if (taskDetail is null)
            {
                outcome = Result<QuickPublishResult>.Fail("读取新建考试任务失败", "NOT_FOUND");
                throw new QuickPublishRollbackException();
            }

            var publishTask = await tasks.PublishAsync(taskDetail.Id, new ExamTaskActionInput(taskDetail.RowVersion));
            if (!publishTask.IsSuccess)
            {
                outcome = Result<QuickPublishResult>.Fail(publishTask.Error ?? "发布考试任务失败", publishTask.Code);
                throw new QuickPublishRollbackException();
            }

            long? batchId = null;
            if (req.AssignWholeDepartment)
            {
                taskDetail = await tasks.GetDetailAsync(taskDetail.Id);
                if (taskDetail is not null)
                {
                    var idempKey = $"qp-assign-{Guid.NewGuid():N}";
                    var reqHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes($"{taskDetail.Id}:{req.DepartmentId}:{req.IncludeDescendants}:{idempKey}"))).ToLowerInvariant();
                    var assignMeta = new ApiAuditMetadata(idempKey, reqHash, "POST", "/exams/quick-publish");
                    var assignRes = await assignments.AssignAsync(
                        taskDetail.Id,
                        new AssignExamInput(
                            new AssignmentTargetInput([], [req.DepartmentId], req.IncludeDescendants, false, true),
                            taskDetail.RowVersion),
                        assignMeta);
                    if (assignRes.IsSuccess)
                    {
                        batchId = assignRes.Value?.BatchId;
                    }
                    else if (assignRes.Code != "NO_TARGETS")
                    {
                        outcome = Result<QuickPublishResult>.Fail(assignRes.Error ?? "下发考生失败", assignRes.Code);
                        throw new QuickPublishRollbackException();
                    }
                }
            }

            var publishedResult = new QuickPublishResult(
                paperDetail.Id, paperDetail.VersionId, createTask.Value.TaskId, taskCode, batchId, totalQuestions, totalScore);
            await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, publishedResult),
                "exam.quick_publish", "ExamTask", createTask.Value.TaskId,
                after: new { publishedResult.PaperId, publishedResult.PaperVersionId, publishedResult.TaskId, publishedResult.TaskCode, publishedResult.BatchId });
            await db.SaveChangesAsync();
            outcome = Result<QuickPublishResult>.Ok(publishedResult);
        });
        }
        catch (ExamIdempotencyConflictException)
        {
            return Result<QuickPublishResult>.Fail("同一幂等键不能用于不同发考参数，请刷新页面后重试", "IDEMPOTENCY_CONFLICT");
        }
        catch (QuickPublishRollbackException)
        {
            // 受控回滚：outcome 已写入具体业务失败原因。
        }

        return outcome ?? Result<QuickPublishResult>.Fail("一键发布未能完成", "UNKNOWN_ERROR");
    }

    private static ApiAuditMetadata? BuildIdempotencyMetadata(QuickPublishRequest req, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey)) return null;
        var canonical = JsonSerializer.Serialize(new
        {
            req.DepartmentId, req.BankId, Title = normalizedTitle, req.SelectionMode,
            req.IncludeSingle, req.SingleCount, req.SingleScore,
            req.IncludeMultiple, req.MultipleCount, req.MultipleScore,
            req.IncludeTrueFalse, req.TrueFalseCount, req.TrueFalseScore,
            req.AvailableFrom, req.AvailableTo, req.DurationMinutes, req.AttemptLimit,
            req.PassScore, req.ResultReleasePolicy, req.AssignWholeDepartment, req.IncludeDescendants
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new ApiAuditMetadata(req.IdempotencyKey.Trim(), hash, "POST", "/exams/quick-publish");
    }

    private sealed class QuickPublishRollbackException : Exception;
}
