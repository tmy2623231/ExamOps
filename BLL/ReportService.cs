using System.Text.Json;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Infra.Export;
using ExamOps.Infra.Storage;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.BLL;

public sealed class ReportService(
    IUnitOfWork uow,
    ReportRepository reports,
    AuditRepository audit,
    OutboxRepository outbox,
    ExamIdempotencyService idempotency,
    DataScopeService scope,
    ICurrentUser currentUser,
    IClock clock,
    IFileStorage storage,
    TimeSpan exportRetention,
    ILogger<ReportService> logger)
{
    private const int MaxRows = 100000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ReportColumn[] SummaryColumns =
    [
        new("taskCode", "任务编码"), new("taskTitle", "任务名称"), new("resultStatus", "成绩状态"),
        new("releaseNo", "发布批次", ReportColumnKind.Integer), new("publishedAt", "发布时间", ReportColumnKind.DateTime),
        new("assignedCount", "应考人数", ReportColumnKind.Integer), new("startedCount", "已开始人数", ReportColumnKind.Integer),
        new("completedCount", "已完成人数", ReportColumnKind.Integer), new("participationRate", "参与率 (%)", ReportColumnKind.Percent),
        new("completionRate", "完成率 (%)", ReportColumnKind.Percent), new("scoredCount", "计分人数", ReportColumnKind.Integer),
        new("unscoredCount", "无成绩人数", ReportColumnKind.Integer), new("averageScore", "平均分", ReportColumnKind.Decimal),
        new("highestScore", "最高分", ReportColumnKind.Decimal), new("lowestScore", "最低分", ReportColumnKind.Decimal),
        new("passedCount", "及格人数", ReportColumnKind.Integer), new("passRate", "及格率 (%)", ReportColumnKind.Percent)
    ];

    public async Task<IReadOnlyList<ReportTaskOption>> GetTasksAsync()
    {
        var access = await EnsurePermissionAsync(Permissions.ReportRead);
        if (!access.IsSuccess) throw new InvalidOperationException(access.Code);
        var ids = await GetVisibleTaskIdsAsync(Permissions.ReportRead);
        return await uow.Db.ExamTasks.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id)
            .Select(x => new ReportTaskOption(x.Id, x.Title, x.ResultStatus)).ToListAsync();
    }

    public async Task<Result<ReportTable>> GetResultsAsync(long taskId)
    {
        var access = await RequireTaskAsync(taskId, Permissions.ReportRead);
        return !access.IsSuccess ? Result<ReportTable>.Fail(access.Error!, access.Code)
            : await BuildDatasetAsync(taskId, ReportTypes.Results, access.Value!);
    }

    public async Task<Result<ReportTable>> GetSummaryAsync(long taskId)
    {
        var access = await RequireTaskAsync(taskId, Permissions.ReportRead);
        if (!access.IsSuccess) return Result<ReportTable>.Fail(access.Error!, access.Code);
        return await BuildSummaryCoreAsync(access.Value!);
    }

    public async Task<Result<ReportTable>> GetDistributionAsync(long taskId)
    {
        var access = await RequireTaskAsync(taskId, Permissions.ReportRead);
        if (!access.IsSuccess) return Result<ReportTable>.Fail(access.Error!, access.Code);
        var snapshot = await RequirePublishedSnapshotAsync(access.Value!);
        if (!snapshot.IsSuccess) return Result<ReportTable>.Fail(snapshot.Error!, snapshot.Code);
        return Result<ReportTable>.Ok(BuildDistribution(snapshot.Value!, clock.UtcNow));
    }

    public async Task<Result<ReportTable>> GetPassRateAsync(long taskId)
    {
        var access = await RequireTaskAsync(taskId, Permissions.ReportRead);
        if (!access.IsSuccess) return Result<ReportTable>.Fail(access.Error!, access.Code);
        var snapshot = await RequirePublishedSnapshotAsync(access.Value!);
        if (!snapshot.IsSuccess) return Result<ReportTable>.Fail(snapshot.Error!, snapshot.Code);
        return Result<ReportTable>.Ok(BuildPassRate(snapshot.Value!, clock.UtcNow));
    }

    public async Task<Result<ReportTable>> GetDepartmentCompareAsync(DepartmentCompareQuery query)
    {
        var access = await EnsurePermissionAsync(Permissions.ReportRead);
        if (!access.IsSuccess) return Result<ReportTable>.Fail(access.Error!, access.Code);
        if (query.From.HasValue && query.To.HasValue && query.From > query.To)
            return Result<ReportTable>.Fail("开始时间不能晚于结束时间", "VALIDATION_ERROR");
        IReadOnlyList<long> taskIds;
        if (query.TaskId.HasValue)
        {
            var task = await RequireTaskAsync(query.TaskId.Value, Permissions.ReportRead);
            if (!task.IsSuccess) return Result<ReportTable>.Fail(task.Error!, task.Code);
            taskIds = [task.Value!.Id];
        }
        else
        {
            taskIds = await GetVisibleTaskIdsAsync(Permissions.ReportRead);
        }
        return await BuildDepartmentCompareCoreAsync(taskIds, query.From, query.To);
    }

    public async Task<Result<ReportTable>> GetQuestionAccuracyAsync(long questionVersionId, long? taskId)
    {
        var access = await EnsurePermissionAsync(Permissions.ReportRead);
        if (!access.IsSuccess) return Result<ReportTable>.Fail(access.Error!, access.Code);
        IReadOnlyList<long> taskIds;
        if (taskId.HasValue)
        {
            var task = await RequireTaskAsync(taskId.Value, Permissions.ReportRead);
            if (!task.IsSuccess) return Result<ReportTable>.Fail(task.Error!, task.Code);
            taskIds = [task.Value!.Id];
        }
        else
        {
            taskIds = await GetVisibleTaskIdsAsync(Permissions.ReportRead);
        }
        return await BuildQuestionCoreAsync(taskIds, questionVersionId);
    }

    public async Task<Result<ReportTable>> GetTaskQuestionsAsync(long taskId)
    {
        var access = await RequireTaskAsync(taskId, Permissions.ReportRead);
        if (!access.IsSuccess) return Result<ReportTable>.Fail(access.Error!, access.Code);
        return await BuildQuestionCoreAsync([taskId], null);
    }

    public async Task<Result<PagedResult<AnomalyRow>>> GetAnomaliesAsync(long taskId, ReportQuery query)
    {
        var access = await RequireTaskAsync(taskId, Permissions.ReportRead);
        if (!access.IsSuccess) return Result<PagedResult<AnomalyRow>>.Fail(access.Error!, access.Code);
        var pageIndex = Math.Max(1, query.PageIndex);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var source = await reports.GetAnomaliesAsync(uow.Db, taskId, pageIndex, pageSize);
        var rows = source.Items.Select(x => new AnomalyRow(x.Id, x.Source, x.AttemptId, x.AttemptNo,
            x.UserId, x.UserName, x.EventType, ReasonFrom(x), x.ActorId, x.ActorName, x.OccurredAt)).ToArray();
        return Result<PagedResult<AnomalyRow>>.Ok(new PagedResult<AnomalyRow>(rows, source.TotalCount, pageIndex, pageSize));
    }

    public async Task<Result<ExportReportOutput>> ExportAsync(long taskId, ExportReportInput input, ApiAuditMetadata? api = null)
    {
        var access = await RequireTaskAsync(taskId, Permissions.ReportExport);
        if (!access.IsSuccess) return Result<ExportReportOutput>.Fail(access.Error!, access.Code);
        var type = input.ReportType.Trim();
        var format = input.Format.Trim().ToUpperInvariant();
        if (type is not (ReportTypes.Summary or ReportTypes.Distribution or ReportTypes.PassRate
            or ReportTypes.DepartmentCompare or ReportTypes.Questions or ReportTypes.Anomalies or ReportTypes.Results))
            return Result<ExportReportOutput>.Fail("报表类型无效", "VALIDATION_ERROR");
        if (format is not ("CSV" or "XLSX"))
            return Result<ExportReportOutput>.Fail("导出格式仅支持 CSV 或 XLSX", "VALIDATION_ERROR");
        if (currentUser.Id is not { } actorId) return Result<ExportReportOutput>.Fail("请先登录", "UNAUTHENTICATED");

        ExportReportOutput? replay = null;
        try { replay = await idempotency.FindReplayAsync<ExportReportOutput>(uow.Db, api); }
        catch (ExamIdempotencyConflictException) { return Conflict<ExportReportOutput>(); }
        if (replay is not null) return Result<ExportReportOutput>.Ok(replay);

        var dataset = await BuildDatasetAsync(taskId, type, access.Value!);
        if (!dataset.IsSuccess) return Result<ExportReportOutput>.Fail(dataset.Error!, dataset.Code);
        var table = dataset.Value!;
        if (table.Rows.Count > MaxRows) return TooLarge<ExportReportOutput>();
        byte[] content;
        try
        {
            content = format == "CSV" ? ReportFileWriter.WriteCsv(table) : ReportFileWriter.WriteXlsx(table);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "报表文件生成失败。TaskId={TaskId}, ReportType={ReportType}", taskId, type);
            return Result<ExportReportOutput>.Fail("报表内容超出导出文件限制", "EXPORT_TOO_LARGE");
        }

        var extension = format == "CSV" ? ".csv" : ".xlsx";
        var fileName = $"ExamOps-{type}-{clock.UtcNow:yyyyMMddHHmmss}{extension}";
        StoredExportFile stored;
        try { stored = await storage.SaveExportAsync(content, extension); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(ex, "报表文件保存失败。TaskId={TaskId}, ReportType={ReportType}", taskId, type);
            return Result<ExportReportOutput>.Fail("导出文件保存失败", "EXPORT_FAILED");
        }

        ExportReportOutput? result = null;
        var reused = false;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var existing = await idempotency.FindReplayAsync<ExportReportOutput>(db, api);
                if (existing is not null) { result = existing; reused = true; return; }
                var now = clock.UtcNow;
                var entity = new ReportExport
                {
                    TaskId = taskId, ReportType = type, Format = format, FileKey = stored.RelativePath,
                    FileName = fileName, RowCount = table.Rows.Count,
                    FilterJson = JsonSerializer.Serialize(new { taskId, reportType = type, format }, JsonOptions),
                    CreatedBy = actorId, CreatedAt = now, ExpiresAt = now.Add(exportRetention)
                };
                db.ReportExports.Add(entity);
                await db.SaveChangesAsync();
                result = new(entity.Id, fileName, format == "CSV" ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"/api/reports/exports/{entity.Id}", entity.ExpiresAt, entity.RowCount);
                var key = string.IsNullOrWhiteSpace(api?.IdempotencyKey) ? $"report-export:{actorId}:{Guid.NewGuid():N}" : api.IdempotencyKey;
                await audit.WriteAsync(db, clock, idempotency.BuildAuditContext(api, result), "report.export", "ReportExport", entity.Id,
                    after: new { entity.TaskId, entity.ReportType, entity.Format, entity.RowCount, entity.FileKey, entity.ExpiresAt },
                    eventKey: key);
                outbox.Add(db, "report.exported", "ReportExport", entity.Id, result, $"report-exported:{entity.Id}", now);
                await db.SaveChangesAsync();
            });
        }
        catch (ExamIdempotencyConflictException)
        {
            storage.DeleteExport(stored.RelativePath);
            return Conflict<ExportReportOutput>();
        }
        catch (Exception ex)
        {
            storage.DeleteExport(stored.RelativePath);
            logger.LogError(ex, "报表导出事务失败。TaskId={TaskId}, ReportType={ReportType}", taskId, type);
            throw;
        }
        if (reused) storage.DeleteExport(stored.RelativePath);
        return Result<ExportReportOutput>.Ok(result!);
    }

    public async Task<Result<ExportDownload>> DownloadAsync(long exportId)
    {
        var permission = await EnsurePermissionAsync(Permissions.ReportExport);
        if (!permission.IsSuccess) return Result<ExportDownload>.Fail(permission.Error!, permission.Code);
        var entity = await uow.Db.ReportExports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == exportId);
        if (entity is null || entity.ExpiresAt <= clock.UtcNow)
            return Result<ExportDownload>.Fail("导出文件不存在或已过期", "NOT_FOUND");
        if (entity.TaskId is not { } taskId || entity.CreatedBy != currentUser.Id)
            return Result<ExportDownload>.Fail("导出文件不存在或无权访问", "NOT_FOUND");
        var task = await reports.GetTaskAsync(uow.Db, taskId);
        if (task is null || !await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, Permissions.ReportExport))
            return Result<ExportDownload>.Fail("导出文件不存在或无权访问", "NOT_FOUND");

        FileStream stream;
        try { stream = await storage.OpenExportAsync(entity.FileKey); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(ex, "导出文件无法读取。ExportId={ExportId}", exportId);
            return Result<ExportDownload>.Fail("导出文件不存在或已过期", "NOT_FOUND");
        }
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "report.export_download", "ReportExport", exportId,
                    after: new { entity.FileName, entity.Format }, eventKey: $"report-download:{exportId}:{Guid.NewGuid():N}");
                await db.SaveChangesAsync();
            });
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
        return Result<ExportDownload>.Ok(new ExportDownload(entity, stream));
    }

    private async Task<Result<ExamTask>> RequireTaskAsync(long taskId, string permission)
    {
        var access = await EnsurePermissionAsync(permission);
        if (!access.IsSuccess) return Result<ExamTask>.Fail(access.Error!, access.Code);
        var task = await reports.GetTaskAsync(uow.Db, taskId);
        if (task is null || !await scope.CanAccessTaskAsync(task.Id, task.DepartmentId, permission))
            return Result<ExamTask>.Fail("资源不存在或无权访问", "NOT_FOUND");
        return Result<ExamTask>.Ok(task);
    }

    private async Task<Result> EnsurePermissionAsync(string permission)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated) return Result.Fail("请先登录", "UNAUTHENTICATED");
        return currentUser.HasPermission(permission) ? Result.Ok() : Result.Fail("权限不足", "PERMISSION_DENIED");
    }

    private async Task<IReadOnlyList<long>> GetVisibleTaskIdsAsync(string permission)
    {
        if (scope.HasGlobalAccess(permission))
            return await uow.Db.ExamTasks.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
        var departments = await scope.GetAccessibleDepartmentIdsAsync(permission) ?? [];
        var assigned = await scope.GetAssignedTaskIdsAsync(permission);
        if (departments.Count == 0 && assigned.Count == 0) return [];
        return await uow.Db.ExamTasks.AsNoTracking()
            .Where(x => departments.Contains(x.DepartmentId) || assigned.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
    }

    private async Task<Result<PublishedSnapshot>> RequirePublishedSnapshotAsync(ExamTask task)
    {
        var snapshot = await reports.GetPublishedSnapshotAsync(uow.Db, task.Id);
        return snapshot is null
            ? Result<PublishedSnapshot>.Fail("成绩当前未发布", "RESULT_NOT_PUBLISHED")
            : Result<PublishedSnapshot>.Ok(snapshot);
    }

    private async Task<Result<ReportTable>> BuildSummaryCoreAsync(ExamTask task)
    {
        var counts = await reports.GetSummaryCountsAsync(uow.Db, task.Id);
        var snapshot = await reports.GetPublishedSnapshotAsync(uow.Db, task.Id);
        var scores = snapshot?.Items.Where(x => x.FinalScore.HasValue).Select(x => x.FinalScore!.Value).ToArray() ?? [];
        var passed = snapshot?.Release.PassScore.HasValue == true ? scores.Count(x => x >= snapshot.Release.PassScore!.Value) : (int?)null;
        var row = Row(
            ("taskCode", task.Code), ("taskTitle", task.Title), ("resultStatus", task.ResultStatus),
            ("releaseNo", snapshot?.Release.ReleaseNo), ("publishedAt", snapshot?.Release.PublishedAt),
            ("assignedCount", counts.AssignedCount), ("startedCount", counts.StartedCount), ("completedCount", counts.CompletedCount),
            ("participationRate", Ratio(counts.StartedCount, counts.AssignedCount)), ("completionRate", Ratio(counts.CompletedCount, counts.AssignedCount)),
            ("scoredCount", snapshot is null ? (int?)null : scores.Length), ("unscoredCount", snapshot is null ? (int?)null : snapshot.Items.Count - scores.Length),
            ("averageScore", scores.Length == 0 ? null : decimal.Round(scores.Average(), 2, MidpointRounding.AwayFromZero)),
            ("highestScore", scores.Length == 0 ? null : scores.Max()), ("lowestScore", scores.Length == 0 ? null : scores.Min()),
            ("passedCount", passed), ("passRate", passed.HasValue ? Ratio(passed.Value, scores.Length) : null));
        return Result<ReportTable>.Ok(new ReportTable($"{task.Title} - 考试汇总", clock.UtcNow, SummaryColumns, [row],
            snapshot is null ? "成绩尚未发布，分数指标不显示；完成率按未撤回安排统计。" : "完成率按未撤回安排统计；分数指标读取当前发布快照。"));
    }

    private static ReportTable BuildDistribution(PublishedSnapshot snapshot, DateTime generatedAt)
    {
        var labels = new[] { "0–59.99%", "60–69.99%", "70–79.99%", "80–89.99%", "90–100%" };
        var counts = new int[labels.Length];
        var scores = snapshot.Items.Where(x => x.FinalScore.HasValue && x.MaxScore > 0).ToArray();
        foreach (var item in scores)
        {
            var percentage = item.FinalScore!.Value / item.MaxScore!.Value * 100m;
            var index = percentage switch
            {
                < 60m => 0,
                < 70m => 1,
                < 80m => 2,
                < 90m => 3,
                _ => 4
            };
            counts[index]++;
        }
        return new ReportTable("成绩分布", generatedAt, DistributionColumns,
            labels.Select((label, index) => Row(("band", label), ("count", counts[index]), ("percentage", Ratio(counts[index], scores.Length)))).ToArray(),
            "按每安排最终分占本人满分的百分比分组；无有效成绩者不进入分母。");
    }

    private static ReportTable BuildPassRate(PublishedSnapshot snapshot, DateTime generatedAt)
    {
        var scores = snapshot.Items.Where(x => x.FinalScore.HasValue).Select(x => x.FinalScore!.Value).ToArray();
        var passed = snapshot.Release.PassScore.HasValue ? scores.Count(x => x >= snapshot.Release.PassScore.Value) : (int?)null;
        return new ReportTable("及格率", generatedAt, PassRateColumns,
            [Row(("passScore", snapshot.Release.PassScore), ("scoredCount", scores.Length), ("passedCount", passed),
                ("failedCount", passed.HasValue ? scores.Length - passed.Value : null), ("passRate", passed.HasValue ? Ratio(passed.Value, scores.Length) : null))],
            snapshot.Release.PassScore.HasValue ? "分母为有最终成绩的安排；未设置及格线时不判断及格。" : "本任务未设置及格线。");
    }

    private async Task<Result<ReportTable>> BuildDepartmentCompareCoreAsync(IReadOnlyList<long> taskIds, DateTime? from, DateTime? to)
    {
        if (taskIds.Count == 0) return Result<ReportTable>.Ok(new ReportTable("部门对比", clock.UtcNow, DepartmentColumns, [], "当前范围没有可统计的任务。"));
        var items = await reports.GetPublishedItemsAsync(uow.Db, taskIds, from, to, MaxRows + 1);
        if (items.Count > MaxRows) return TooLarge<ReportTable>();
        var rows = items.GroupBy(x => x.DepartmentId).OrderBy(x => x.Key).Select(group =>
        {
            var scores = group.Where(x => x.FinalScore.HasValue).Select(x => x.FinalScore!.Value).ToArray();
            var passed = group.Count(x => x.Passed == true);
            var hasPassScore = group.Any(x => x.Passed.HasValue);
            return Row(("departmentId", group.Key), ("departmentName", group.First().DepartmentName ?? group.Key.ToString()),
                ("taskCount", group.Select(x => x.TaskId).Distinct().Count()), ("assignmentCount", group.Count()),
                ("scoredCount", scores.Length), ("averageScore", scores.Length == 0 ? null : decimal.Round(scores.Average(), 2, MidpointRounding.AwayFromZero)),
                ("highestScore", scores.Length == 0 ? null : scores.Max()), ("lowestScore", scores.Length == 0 ? null : scores.Min()),
                ("passedCount", hasPassScore ? passed : null), ("passRate", hasPassScore ? Ratio(passed, scores.Length) : null));
        }).ToArray();
        return Result<ReportTable>.Ok(new ReportTable("部门对比", clock.UtcNow, DepartmentColumns, rows,
            "仅统计当前已发布快照；部门使用安排时的部门快照，时间筛选按发布时间。"));
    }

    private async Task<Result<ReportTable>> BuildQuestionCoreAsync(IReadOnlyList<long> taskIds, long? questionVersionId)
    {
        if (taskIds.Count == 0) return Result<ReportTable>.Ok(new ReportTable("题目正确率", clock.UtcNow, QuestionColumns, [], "当前范围没有可统计的任务。"));
        var attempts = await reports.GetQuestionAttemptsAsync(uow.Db, taskIds, MaxRows + 1);
        if (attempts.Count > MaxRows) return TooLarge<ReportTable>();
        var answers = await reports.GetQuestionAnswersAsync(uow.Db, taskIds, questionVersionId, MaxRows + 1);
        if (answers.Count > MaxRows) return TooLarge<ReportTable>();
        var answerMap = answers.ToDictionary(x => (x.AttemptId, x.QuestionVersionId));
        var stats = new Dictionary<long, QuestionStat>();
        foreach (var attempt in attempts)
        {
            HashSet<long> selected;
            try
            {
                using var document = JsonDocument.Parse(attempt.PaperSnapshotJson);
                if (!document.RootElement.TryGetProperty("sections", out var sections)) throw new InvalidDataException("缺少 sections");
                selected = sections.EnumerateArray().SelectMany(section => section.GetProperty("questions").EnumerateArray())
                    .Select(x => x.GetProperty("questionVersionId").GetInt64()).ToHashSet();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException or KeyNotFoundException)
            {
                logger.LogError(ex, "答卷抽题快照无法解析。AttemptId={AttemptId}", attempt.AttemptId);
                return Result<ReportTable>.Fail("答卷抽题快照存在异常，无法生成报表", "REPORT_DATA_INVALID");
            }
            foreach (var versionId in selected)
            {
                if (questionVersionId.HasValue && versionId != questionVersionId.Value) continue;
                var stat = stats.GetValueOrDefault(versionId) ?? new QuestionStat();
                stat.AttemptCount++;
                if (answerMap.TryGetValue((attempt.AttemptId, versionId), out var answer))
                {
                    if (answer.HasAnswer) stat.AnsweredCount++;
                    stat.ScoreTotal += answer.Score;
                    stat.MaxScore = Math.Max(stat.MaxScore, answer.MaxScore);
                    if (answer.GradingStatus == "Invalid") stat.InvalidCount++;
                    if (answer.GradingStatus == "Graded" && answer.MaxScore > 0 && answer.Score >= answer.MaxScore) stat.CorrectCount++;
                }
                stats[versionId] = stat;
            }
        }
        var metadata = await reports.GetQuestionMetadataAsync(uow.Db, stats.Keys.ToArray());
        var metaMap = metadata.ToDictionary(x => x.VersionId);
        var rows = stats.OrderBy(x => Ratio(x.Value.CorrectCount, x.Value.AttemptCount)).ThenBy(x => x.Key).Select(pair =>
        {
            var meta = metaMap.GetValueOrDefault(pair.Key);
            var stat = pair.Value;
            return Row(("questionVersionId", pair.Key), ("questionId", meta?.QuestionId), ("questionCode", meta?.ExternalCode),
                ("questionType", meta?.Type), ("stem", meta?.Stem), ("attemptCount", stat.AttemptCount), ("answeredCount", stat.AnsweredCount),
                ("unansweredCount", stat.AttemptCount - stat.AnsweredCount), ("invalidCount", stat.InvalidCount), ("correctCount", stat.CorrectCount),
                ("incorrectCount", stat.AttemptCount - stat.CorrectCount), ("accuracy", Ratio(stat.CorrectCount, stat.AttemptCount)),
                ("averageScore", stat.AttemptCount == 0 ? null : decimal.Round(stat.ScoreTotal / stat.AttemptCount, 2, MidpointRounding.AwayFromZero)),
                ("maxScore", stat.MaxScore));
        }).ToArray();
        return Result<ReportTable>.Ok(new ReportTable("题目正确率", clock.UtcNow, QuestionColumns, rows,
            "仅统计 Graded 且非 Voided 答卷实际抽取的题目；未答和格式异常均计错，随机池未抽中题不统计。"));
    }

    private async Task<Result<ReportTable>> BuildDatasetAsync(long taskId, string type, ExamTask task)
    {
        switch (type)
        {
            case ReportTypes.Summary: return await BuildSummaryCoreAsync(task);
            case ReportTypes.Distribution:
            {
                var snapshot = await RequirePublishedSnapshotAsync(task);
                return snapshot.IsSuccess ? Result<ReportTable>.Ok(BuildDistribution(snapshot.Value!, clock.UtcNow))
                    : Result<ReportTable>.Fail(snapshot.Error!, snapshot.Code);
            }
            case ReportTypes.PassRate:
            {
                var snapshot = await RequirePublishedSnapshotAsync(task);
                return snapshot.IsSuccess ? Result<ReportTable>.Ok(BuildPassRate(snapshot.Value!, clock.UtcNow))
                    : Result<ReportTable>.Fail(snapshot.Error!, snapshot.Code);
            }
            case ReportTypes.DepartmentCompare: return await BuildDepartmentCompareCoreAsync([taskId], null, null);
            case ReportTypes.Questions: return await BuildQuestionCoreAsync([taskId], null);
            case ReportTypes.Anomalies:
            {
                var source = await reports.GetAnomaliesAsync(uow.Db, taskId, 1, MaxRows + 1);
                var rows = source.Items.Select(x => Row(("id", x.Id), ("source", x.Source), ("attemptId", x.AttemptId),
                    ("attemptNo", x.AttemptNo), ("userId", x.UserId), ("userName", x.UserName), ("eventType", x.EventType),
                    ("reason", ReasonFrom(x)), ("actorId", x.ActorId), ("actorName", x.ActorName), ("occurredAt", x.OccurredAt))).ToArray();
                return Result<ReportTable>.Ok(new ReportTable("异常记录", clock.UtcNow, AnomalyColumns, rows, "包含客观题格式异常、复核事件、调分与作废事件。"));
            }
            case ReportTypes.Results:
            {
                var snapshot = await RequirePublishedSnapshotAsync(task);
                if (!snapshot.IsSuccess) return Result<ReportTable>.Fail(snapshot.Error!, snapshot.Code);
                var items = await reports.GetPublishedItemsAsync(uow.Db, [taskId], null, null, MaxRows + 1);
                var rows = items.Select(x => Row(("taskCode", x.TaskCode), ("taskTitle", x.TaskTitle), ("releaseNo", x.ReleaseNo),
                    ("publishedAt", x.PublishedAt), ("assignmentId", x.AssignmentId), ("userId", x.UserId), ("userName", x.UserName),
                    ("departmentName", x.DepartmentName), ("attemptId", x.AttemptId), ("finalScore", x.FinalScore),
                    ("maxScore", x.MaxScore), ("passed", x.Passed))).ToArray();
                return Result<ReportTable>.Ok(new ReportTable("成绩明细", clock.UtcNow, ResultColumns, rows, "读取当前发布快照；撤回后不可导出成绩。"));
            }
            default: return Result<ReportTable>.Fail("报表类型无效", "VALIDATION_ERROR");
        }
    }

    private static string ReasonFrom(AnomalySourceRow row)
    {
        try
        {
            using var document = JsonDocument.Parse(row.PayloadJson);
            foreach (var name in new[] { "reason", "Reason" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
        }
        catch (JsonException) { return "复核事件"; }
        return "复核事件";
    }

    private static decimal? Ratio(int numerator, int denominator) => denominator == 0 ? null : decimal.Round((decimal)numerator / denominator, 4, MidpointRounding.AwayFromZero);
    private static ReportRow Row(params (string Key, object? Value)[] values) => new(values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
    private static Result<T> Conflict<T>() => Result<T>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private static Result<T> TooLarge<T>() => Result<T>.Fail("报表超过 100000 行，请缩小范围", "EXPORT_TOO_LARGE");

    private static readonly ReportColumn[] DistributionColumns =
    [new("band", "分数段"), new("count", "人数", ReportColumnKind.Integer), new("percentage", "占比 (%)", ReportColumnKind.Percent)];
    private static readonly ReportColumn[] PassRateColumns =
    [new("passScore", "及格分", ReportColumnKind.Decimal), new("scoredCount", "计分人数", ReportColumnKind.Integer),
        new("passedCount", "及格人数", ReportColumnKind.Integer), new("failedCount", "不及格人数", ReportColumnKind.Integer),
        new("passRate", "及格率 (%)", ReportColumnKind.Percent)];
    private static readonly ReportColumn[] DepartmentColumns =
    [new("departmentId", "部门 ID", ReportColumnKind.Integer), new("departmentName", "部门"), new("taskCount", "任务数", ReportColumnKind.Integer),
        new("assignmentCount", "安排数", ReportColumnKind.Integer), new("scoredCount", "计分人数", ReportColumnKind.Integer),
        new("averageScore", "平均分", ReportColumnKind.Decimal), new("highestScore", "最高分", ReportColumnKind.Decimal),
        new("lowestScore", "最低分", ReportColumnKind.Decimal), new("passedCount", "及格人数", ReportColumnKind.Integer),
        new("passRate", "及格率 (%)", ReportColumnKind.Percent)];
    private static readonly ReportColumn[] QuestionColumns =
    [new("questionVersionId", "题目版本 ID", ReportColumnKind.Integer), new("questionId", "题目 ID", ReportColumnKind.Integer),
        new("questionCode", "题目编码"), new("questionType", "题型"), new("stem", "题干"), new("attemptCount", "作答人次", ReportColumnKind.Integer),
        new("answeredCount", "已答人次", ReportColumnKind.Integer), new("unansweredCount", "未答人次", ReportColumnKind.Integer),
        new("invalidCount", "格式异常人次", ReportColumnKind.Integer), new("correctCount", "正确人次", ReportColumnKind.Integer),
        new("incorrectCount", "错误人次", ReportColumnKind.Integer), new("accuracy", "正确率 (%)", ReportColumnKind.Percent),
        new("averageScore", "平均得分", ReportColumnKind.Decimal), new("maxScore", "每题满分", ReportColumnKind.Decimal)];
    private static readonly ReportColumn[] AnomalyColumns =
    [new("id", "记录 ID", ReportColumnKind.Integer), new("source", "来源"), new("attemptId", "答卷 ID", ReportColumnKind.Integer),
        new("attemptNo", "答卷次数", ReportColumnKind.Integer), new("userId", "考生 ID", ReportColumnKind.Integer), new("userName", "考生"),
        new("eventType", "事件类型"), new("reason", "原因"), new("actorId", "操作者 ID", ReportColumnKind.Integer),
        new("actorName", "操作者"), new("occurredAt", "发生时间", ReportColumnKind.DateTime)];
    private static readonly ReportColumn[] ResultColumns =
    [new("taskCode", "任务编码"), new("taskTitle", "任务名称"), new("releaseNo", "发布批次", ReportColumnKind.Integer),
        new("publishedAt", "发布时间", ReportColumnKind.DateTime), new("assignmentId", "安排 ID", ReportColumnKind.Integer),
        new("userId", "考生 ID", ReportColumnKind.Integer), new("userName", "考生"), new("departmentName", "部门"),
        new("attemptId", "来源答卷 ID", ReportColumnKind.Integer), new("finalScore", "最终分", ReportColumnKind.Decimal),
        new("maxScore", "满分", ReportColumnKind.Decimal), new("passed", "是否及格")];

    private sealed class QuestionStat
    {
        public int AttemptCount { get; set; }
        public int AnsweredCount { get; set; }
        public int InvalidCount { get; set; }
        public int CorrectCount { get; set; }
        public decimal ScoreTotal { get; set; }
        public decimal MaxScore { get; set; }
    }
}

public sealed record ExportDownload(ReportExport Entity, FileStream Content);
