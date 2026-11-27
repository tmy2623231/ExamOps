using ExamOps.BLL.Validators;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;

namespace ExamOps.BLL;

public sealed class PaperService(
    IUnitOfWork uow,
    PaperRepository papers,
    QuestionRepository questions,
    DataScopeService scope,
    AuditRepository audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<PagedResult<PaperRow>> GetPagedAsync(PaperQuery query)
    {
        await currentUser.LoadAsync();
        RequireRead();
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.PaperRead);
        return await papers.GetPagedAsync(NormalizeQuery(query), allowed);
    }

    public async Task<PaperDetail?> GetDetailAsync(long id, long? versionId = null)
    {
        await currentUser.LoadAsync();
        RequireRead();
        var detail = await papers.GetDetailAsync(uow.Db, id, versionId);
        if (detail is null) return null;
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.PaperRead);
        if (allowed is not null && !allowed.Contains(detail.DepartmentId)) throw new UnauthorizedAccessException("无权查看该试卷");
        return detail;
    }

    public async Task<PagedResult<PaperCandidateRow>> GetCandidatesAsync(PaperCandidateQuery query)
    {
        await currentUser.LoadAsync();
        RequireEditorAndQuestions();
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.QuestionRead);
        return await questions.GetPaperCandidatesAsync(uow.Db, allowed, query with
        {
            PageIndex = Math.Max(1, query.PageIndex), PageSize = Math.Clamp(query.PageSize, 1, 100)
        });
    }

    public async Task<Result<PaperMutationResult>> CreateAsync(CreatePaperInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.PaperCreate)) return PermissionFailure<PaperMutationResult>();
        var validation = PaperValidator.ValidateCreate(input);
        if (!validation.IsSuccess) return Result<PaperMutationResult>.Fail(validation.Error!, validation.Code);
        if (!(await scope.EnsureDepartmentAccessAsync(input.DepartmentId, Permissions.PaperCreate)).IsSuccess)
            return Result<PaperMutationResult>.Fail("无权在该部门创建试卷", "OUT_OF_SCOPE");
        var now = clock.UtcNow;
        long paperId = 0;
        long versionId = 0;
        PaperMutationResult? replay = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                replay = await FindReplayAsync(db, api);
                if (replay is not null) return;
                versionId = await papers.CreateDraftAsync(db, input, ActorId(), now);
                paperId = await db.Papers.AsNoTracking().Where(x => x.Id == db.PaperVersions.Where(v => v.Id == versionId)
                    .Select(v => v.PaperId).First()).Select(x => x.Id).SingleAsync();
                await audit.WriteAsync(db, clock, AuditContext(api, new { paperId, versionId }), "paper.create", "Paper", paperId,
                    after: new { input.Title, input.DepartmentId, VersionId = versionId });
                await db.SaveChangesAsync();
            });
            return Result<PaperMutationResult>.Ok(replay ?? new(paperId, versionId));
        }
        catch (IdempotencyConflictException) { return IdempotencyConflictFailure<PaperMutationResult>(); }
        catch (DbUpdateException ex) when (IsAuditIdempotencyViolation(ex))
        {
            return IdempotencyFailure<PaperMutationResult>();
        }
    }

    public async Task<Result> UpdateDraftAsync(long paperId, long versionId, long paperRowVersion,
        long versionRowVersion, PaperVersionInput input, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.PaperUpdate)) return PermissionFailure();
        var detail = await GetDetailAsync(paperId, versionId);
        if (detail is null) return Result.Fail("试卷版本不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(detail.DepartmentId, Permissions.PaperUpdate)).IsSuccess)
            return Result.Fail("无权修改该试卷", "OUT_OF_SCOPE");
        if (detail.VersionStatus != "Draft" || detail.Status == "Archived") return Result.Fail("只能修改未归档的草稿版本", "DRAFT_REQUIRED");
        var validation = PaperValidator.Validate(input);
        if (!validation.IsSuccess) return validation;
        var categoryFailure = await ValidateBlueprintAsync(input.Sections);
        if (categoryFailure is not null) return categoryFailure;
        var questionIds = input.Sections.SelectMany(x => x.QuestionVersionIds).Distinct().ToArray();
        var questionFailure = await ValidateQuestionsAsync(questionIds);
        if (questionFailure is not null) return questionFailure;
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var replay = await FindReplayAsync(db, api);
                if (replay is not null) { outcome = Result.Ok(); return; }
                var paper = await papers.GetPaperTrackedAsync(db, paperId);
                var version = await papers.GetVersionTrackedAsync(db, versionId);
                if (paper is null || version is null || version.PaperId != paper.Id) { outcome = Result.Fail("试卷版本不存在", "NOT_FOUND"); return; }
                if (paper.RowVersion != paperRowVersion || version.RowVersion != versionRowVersion) { outcome = ConcurrencyFailure(); return; }
                if (version.Status != "Draft" || paper.Status == "Archived") { outcome = Result.Fail("只能修改未归档的草稿版本", "DRAFT_REQUIRED"); return; }
                await papers.UpdateDraftAsync(db, paper, version, input, now);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, AuditContext(api, new { paperId, versionId }), "paper.update", "Paper", paperId,
                    new { VersionId = versionId }, new { input.Title, input.DurationMinutes, SectionCount = input.Sections.Count, version.TotalScore });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (DbUpdateConcurrencyException) { return ConcurrencyFailure(); }
        catch (IdempotencyConflictException) { return IdempotencyConflictFailure(); }
        catch (DbUpdateException ex) when (IsAuditIdempotencyViolation(ex)) { return IdempotencyFailure(); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return Result.Fail("小节顺序或题目成员冲突，请刷新后重试", "DUPLICATE_PAPER_MEMBER"); }
    }

    public async Task<Result> ReplaceSectionsAsync(long paperId, long versionId, long paperRowVersion,
        long versionRowVersion, IReadOnlyList<PaperSectionInput> sections, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.PaperUpdate)) return PermissionFailure();
        var detail = await GetDetailAsync(paperId, versionId);
        if (detail is null) return Result.Fail("试卷版本不存在", "NOT_FOUND");
        return await UpdateDraftAsync(paperId, versionId, paperRowVersion, versionRowVersion,
            new(detail.VersionTitle, detail.DurationMinutes, sections), api);
    }

    public async Task<Result<PaperMutationResult>> CopyDraftAsync(long paperId, long paperRowVersion, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.PaperUpdate)) return PermissionFailure<PaperMutationResult>();
        var detail = await GetDetailAsync(paperId);
        if (detail is null) return Result<PaperMutationResult>.Fail("试卷不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(detail.DepartmentId, Permissions.PaperUpdate)).IsSuccess)
            return Result<PaperMutationResult>.Fail("无权复制该试卷", "OUT_OF_SCOPE");
        if (detail.Status == "Archived") return Result<PaperMutationResult>.Fail("已归档试卷不能创建修订稿", "PAPER_ARCHIVED");
        var now = clock.UtcNow;
        long versionId = 0;
        Result<PaperMutationResult>? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var replay = await FindReplayAsync(db, api);
                if (replay is not null) { outcome = Result<PaperMutationResult>.Ok(replay); return; }
                var paper = await papers.GetPaperTrackedAsync(db, paperId);
                if (paper is null) { outcome = Result<PaperMutationResult>.Fail("试卷不存在", "NOT_FOUND"); return; }
                if (paper.RowVersion != paperRowVersion) { outcome = ConcurrencyFailure<PaperMutationResult>(); return; }
                if (paper.Status == "Archived") { outcome = Result<PaperMutationResult>.Fail("已归档试卷不能创建修订稿", "PAPER_ARCHIVED"); return; }
                var version = await papers.CopyDraftAsync(db, paperId, now);
                if (version is null) { outcome = Result<PaperMutationResult>.Fail("试卷没有可复制的版本", "NOT_FOUND"); return; }
                versionId = version.Id;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, AuditContext(api, new { paperId, versionId }), "paper.copy_draft", "Paper", paperId,
                    after: new { VersionId = versionId, VersionNo = version.VersionNo });
                await db.SaveChangesAsync();
                outcome = Result<PaperMutationResult>.Ok(new(paperId, versionId));
            });
            return outcome!;
        }
        catch (IdempotencyConflictException) { return IdempotencyConflictFailure<PaperMutationResult>(); }
        catch (DbUpdateException ex) when (IsAuditIdempotencyViolation(ex)) { return IdempotencyFailure<PaperMutationResult>(); }
        catch (DbUpdateConcurrencyException) { return ConcurrencyFailure<PaperMutationResult>(); }
    }

    public async Task<Result<PaperMutationResult>> ApplyBlueprintAsync(long paperId, long versionId,
        long sectionId, long paperRowVersion, long versionRowVersion, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.PaperUpdate)) return PermissionFailure<PaperMutationResult>();
        RequireEditorAndQuestions();
        var detail = await GetDetailAsync(paperId, versionId);
        if (detail is null) return Result<PaperMutationResult>.Fail("试卷版本不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(detail.DepartmentId, Permissions.PaperUpdate)).IsSuccess)
            return Result<PaperMutationResult>.Fail("无权修改该试卷", "OUT_OF_SCOPE");
        var section = detail.Sections.FirstOrDefault(x => x.Id == sectionId);
        if (section is null || section.SelectionMode != "Random") return Result<PaperMutationResult>.Fail("随机小节不存在", "NOT_FOUND");
        if (detail.VersionStatus != "Draft" || detail.Status == "Archived") return Result<PaperMutationResult>.Fail("只能对草稿应用蓝图", "DRAFT_REQUIRED");
        var candidates = await questions.GetPaperCandidatesAsync(uow.Db, await scope.GetAccessibleDepartmentIdsAsync(Permissions.QuestionRead),
            new PaperCandidateQuery(section.BankId, section.CategoryId, section.QuestionType, section.Difficulty, null, 1, 10000));
        if (candidates.TotalCount > candidates.Items.Count) return Result<PaperMutationResult>.Fail("候选池超过 10000 题，请缩小蓝图范围", "CANDIDATE_POOL_TOO_LARGE");
        var ids = candidates.Items.Select(x => x.QuestionVersionId).ToArray();
        var now = clock.UtcNow;
        Result<PaperMutationResult>? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var replay = await FindReplayAsync(db, api);
                if (replay is not null) { outcome = Result<PaperMutationResult>.Ok(replay); return; }
                var paper = await papers.GetPaperTrackedAsync(db, paperId);
                var version = await papers.GetVersionTrackedAsync(db, versionId);
                var trackedSection = sectionId == 0 ? null : await papers.GetSectionTrackedAsync(db, sectionId);
                if (paper is null || version is null || trackedSection is null || version.PaperId != paper.Id || trackedSection.PaperVersionId != version.Id)
                { outcome = Result<PaperMutationResult>.Fail("随机小节不存在", "NOT_FOUND"); return; }
                if (paper.RowVersion != paperRowVersion || version.RowVersion != versionRowVersion)
                { outcome = ConcurrencyFailure<PaperMutationResult>(); return; }
                if (version.Status != "Draft" || trackedSection.SelectionMode != "Random")
                { outcome = Result<PaperMutationResult>.Fail("只能对草稿随机小节应用蓝图", "DRAFT_REQUIRED"); return; }
                await papers.ApplyBlueprintAsync(db, paper, version, trackedSection, ids, now);
                await audit.WriteAsync(db, clock, AuditContext(api, new { paperId, versionId, sectionId }), "paper.blueprint.apply", "PaperSection", sectionId,
                    after: new { VersionId = versionId, CandidateCount = ids.Length });
                await db.SaveChangesAsync();
                outcome = Result<PaperMutationResult>.Ok(new(paperId, versionId));
            });
            return outcome!;
        }
        catch (IdempotencyConflictException) { return IdempotencyConflictFailure<PaperMutationResult>(); }
        catch (DbUpdateException ex) when (IsAuditIdempotencyViolation(ex)) { return IdempotencyFailure<PaperMutationResult>(); }
        catch (DbUpdateConcurrencyException) { return ConcurrencyFailure<PaperMutationResult>(); }
    }

    public async Task<Result> AddQuestionAsync(long paperId, long versionId, long sectionId, long questionVersionId,
        long paperRowVersion, long versionRowVersion, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.PaperUpdate)) return PermissionFailure();
        RequireEditorAndQuestions();
        var detail = await GetDetailAsync(paperId, versionId);
        if (detail is null || !detail.Sections.Any(x => x.Id == sectionId)) return Result.Fail("小节不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(detail.DepartmentId, Permissions.PaperUpdate)).IsSuccess) return Result.Fail("无权修改该试卷", "OUT_OF_SCOPE");
        if (detail.VersionStatus != "Draft" || detail.Status == "Archived") return Result.Fail("只能修改草稿试卷", "DRAFT_REQUIRED");
        var invalid = await ValidateQuestionsAsync([questionVersionId]);
        if (invalid is not null) return invalid;
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var replay = await FindReplayAsync(db, api);
                if (replay is not null) { outcome = Result.Ok(); return; }
                var paper = await papers.GetPaperTrackedAsync(db, paperId);
                var version = await papers.GetVersionTrackedAsync(db, versionId);
                var section = await papers.GetSectionTrackedAsync(db, sectionId);
                if (paper is null || version is null || section is null || version.PaperId != paper.Id || section.PaperVersionId != version.Id)
                { outcome = Result.Fail("小节不存在", "NOT_FOUND"); return; }
                if (paper.RowVersion != paperRowVersion || version.RowVersion != versionRowVersion) { outcome = ConcurrencyFailure(); return; }
                if (section.SelectionMode == "Fixed" && PaperValidator.ValidateTotal(version.TotalScore + section.ScorePerQuestion) is { } totalFailure)
                { outcome = totalFailure; return; }
                if (detail.Questions.Any(x => x.SectionId == sectionId && x.QuestionVersionId == questionVersionId))
                { outcome = Result.Fail("该题目已在此小节中", "DUPLICATE_QUESTION"); return; }
                await papers.AddQuestionAsync(db, paper, version, section, questionVersionId, now);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, AuditContext(api, new { paperId, versionId }), "paper.question.add", "PaperQuestion", questionVersionId,
                    after: new { PaperId = paperId, VersionId = versionId, SectionId = sectionId });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (IdempotencyConflictException) { return IdempotencyConflictFailure(); }
        catch (DbUpdateException ex) when (IsAuditIdempotencyViolation(ex)) { return IdempotencyFailure(); }
        catch (DbUpdateConcurrencyException) { return ConcurrencyFailure(); }
    }

    public async Task<Result> DeleteQuestionAsync(long paperId, long versionId, long paperQuestionId,
        long paperRowVersion, long versionRowVersion, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.PaperUpdate)) return PermissionFailure();
        var detail = await GetDetailAsync(paperId, versionId);
        if (detail is null || !detail.Questions.Any(x => x.Id == paperQuestionId)) return Result.Fail("试卷题目不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(detail.DepartmentId, Permissions.PaperUpdate)).IsSuccess) return Result.Fail("无权修改该试卷", "OUT_OF_SCOPE");
        if (detail.VersionStatus != "Draft" || detail.Status == "Archived") return Result.Fail("只能修改草稿试卷", "DRAFT_REQUIRED");
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var replay = await FindReplayAsync(db, api);
                if (replay is not null) { outcome = Result.Ok(); return; }
                var paper = await papers.GetPaperTrackedAsync(db, paperId);
                var version = await papers.GetVersionTrackedAsync(db, versionId);
                var question = await papers.GetPaperQuestionTrackedAsync(db, paperQuestionId);
                if (paper is null || version is null || question is null || version.PaperId != paper.Id || question.PaperVersionId != version.Id)
                { outcome = Result.Fail("试卷题目不存在", "NOT_FOUND"); return; }
                if (paper.RowVersion != paperRowVersion || version.RowVersion != versionRowVersion) { outcome = ConcurrencyFailure(); return; }
                await papers.DeleteQuestionAsync(db, paper, version, question, now);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, AuditContext(api, new { paperId, versionId }), "paper.question.delete", "PaperQuestion", paperQuestionId,
                    new { PaperId = paperId, VersionId = versionId, SectionId = question.SectionId, question.QuestionVersionId });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (IdempotencyConflictException) { return IdempotencyConflictFailure(); }
        catch (DbUpdateException ex) when (IsAuditIdempotencyViolation(ex)) { return IdempotencyFailure(); }
        catch (DbUpdateConcurrencyException) { return ConcurrencyFailure(); }
    }

    public async Task<Result> PublishAsync(long paperId, PaperPublishInput input, long expectedPaperRowVersion, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.PaperPublish)) return PermissionFailure();
        var detail = await GetDetailAsync(paperId, input.VersionId);
        if (detail is null) return Result.Fail("试卷版本不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(detail.DepartmentId, Permissions.PaperPublish)).IsSuccess) return Result.Fail("无权发布该试卷", "OUT_OF_SCOPE");
        if (detail.VersionStatus != "Draft" || detail.Status == "Archived") return Result.Fail("只能发布未归档的草稿版本", "DRAFT_REQUIRED");
        var blocking = detail.Preview.Issues.FirstOrDefault(x => x.Code != "POOL_OVERLAP");
        if (blocking is not null) return Result.Fail(blocking.Message, blocking.Code);
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var replay = await FindReplayAsync(db, api);
                if (replay is not null) { outcome = Result.Ok(); return; }
                var paper = await papers.GetPaperTrackedAsync(db, paperId);
                var version = await papers.GetVersionTrackedAsync(db, input.VersionId);
                if (paper is null || version is null || version.PaperId != paper.Id) { outcome = Result.Fail("试卷版本不存在", "NOT_FOUND"); return; }
                if (paper.RowVersion != expectedPaperRowVersion || version.RowVersion != input.RowVersion) { outcome = ConcurrencyFailure(); return; }
                if (!await papers.PublishAsync(db, paper, version, now, ActorId())) { outcome = Result.Fail("只能发布未归档的草稿版本", "DRAFT_REQUIRED"); return; }
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, AuditContext(api, new { paperId, versionId = version.Id }), "paper.publish", "Paper", paperId,
                    after: new { VersionId = version.Id, version.TotalScore, version.DurationMinutes });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (IdempotencyConflictException) { return IdempotencyConflictFailure(); }
        catch (DbUpdateException ex) when (IsAuditIdempotencyViolation(ex)) { return IdempotencyFailure(); }
        catch (DbUpdateConcurrencyException) { return ConcurrencyFailure(); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        { return Result.Fail("试卷校验未通过，请检查小节、候选题与总分", "PAPER_NOT_READY"); }
    }

    public async Task<Result> ArchiveAsync(long paperId, long expectedRowVersion, ApiAuditMetadata? api = null)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.PaperArchive)) return PermissionFailure();
        var detail = await GetDetailAsync(paperId);
        if (detail is null) return Result.Fail("试卷不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(detail.DepartmentId, Permissions.PaperArchive)).IsSuccess) return Result.Fail("无权归档该试卷", "OUT_OF_SCOPE");
        if (detail.Status == "Archived") return Result.Fail("试卷已归档", "PAPER_ARCHIVED");
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var replay = await FindReplayAsync(db, api);
                if (replay is not null) { outcome = Result.Ok(); return; }
                var paper = await papers.GetPaperTrackedAsync(db, paperId);
                if (paper is null) { outcome = Result.Fail("试卷不存在", "NOT_FOUND"); return; }
                if (paper.RowVersion != expectedRowVersion) { outcome = ConcurrencyFailure(); return; }
                if (!await papers.ArchiveAsync(db, paper, now)) { outcome = Result.Fail("试卷已归档", "PAPER_ARCHIVED"); return; }
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, AuditContext(api, new { paperId, versionId = paper.CurrentVersionId ?? 0 }), "paper.archive", "Paper", paperId,
                    after: new { paper.Status, paper.ArchivedAt });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (IdempotencyConflictException) { return IdempotencyConflictFailure(); }
        catch (DbUpdateException ex) when (IsAuditIdempotencyViolation(ex)) { return IdempotencyFailure(); }
        catch (DbUpdateConcurrencyException) { return ConcurrencyFailure(); }
    }

    private async Task<Result?> ValidateBlueprintAsync(IReadOnlyList<PaperSectionInput> sections)
    {
        foreach (var section in sections.Where(x => x.SelectionMode == "Random"))
        {
            if (!section.BankId.HasValue) return Result.Fail("随机小节需要选择题库", "BANK_REQUIRED");
            if (section.CategoryId.HasValue && !await questions.CategoriesBelongToBankAsync(uow.Db, section.BankId.Value, [section.CategoryId.Value]))
                return Result.Fail("分类已停用或不属于所选题库", "INVALID_CATEGORY");
        }
        return null;
    }

    private async Task<Result?> ValidateQuestionsAsync(IReadOnlyList<long> questionVersionIds)
    {
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.QuestionRead);
        return await questions.PublishedPaperCandidatesExistAsync(uow.Db, allowed, questionVersionIds)
            ? null : Result.Fail("只能选择可见题库中已发布的当前题目版本", "INVALID_QUESTION_VERSION");
    }

    private void RequireRead()
    {
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.PaperRead))
            throw new UnauthorizedAccessException("无权查看试卷");
    }

    private bool RequirePermission(string permission)
        => currentUser.IsAuthenticated && currentUser.HasPermission(permission);

    private void RequireEditorAndQuestions()
    {
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.PaperUpdate) || !currentUser.HasPermission(Permissions.QuestionRead))
            throw new UnauthorizedAccessException("无权浏览组卷候选题");
    }

    private async Task<PaperMutationResult?> FindReplayAsync(ExamDbContext db, ApiAuditMetadata? api)
    {
        if (api is null || string.IsNullOrWhiteSpace(api.IdempotencyKey)) return null;
        var lockKey = $"{ActorId()}|{api.HttpMethod}|{api.RouteTemplate}|{api.IdempotencyKey}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))");
        var existing = await audit.FindApiAuditAsync(db, ActorId(), api);
        if (existing is null) return null;
        if (!string.Equals(existing.RequestHash, api.RequestHash, StringComparison.Ordinal)) throw new IdempotencyConflictException();
        return JsonSerializer.Deserialize<PaperMutationResult>(existing.ResultRefJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class IdempotencyConflictException : Exception;

    private AuditContext AuditContext(ApiAuditMetadata? api, object resultRef)
    {
        var context = currentUser.GetAuditContext();
        return api is null ? context : context with
        {
            IdempotencyKey = api.IdempotencyKey, RequestHash = api.RequestHash,
            HttpMethod = api.HttpMethod, RouteTemplate = api.RouteTemplate, ResultRef = resultRef
        };
    }

    private static Result IdempotencyFailure() => Result.Fail("请求已处理，正在返回原结果", "IDEMPOTENCY_REPLAY");
    private static Result<T> IdempotencyFailure<T>() => Result<T>.Fail("请求已处理，正在返回原结果", "IDEMPOTENCY_REPLAY");
    private static Result IdempotencyConflictFailure() => Result.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private static Result<T> IdempotencyConflictFailure<T>() => Result<T>.Fail("同一幂等键不能用于不同请求", "IDEMPOTENCY_CONFLICT");
    private static bool IsAuditIdempotencyViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            && (exception.InnerException as PostgresException)?.ConstraintName == "ux_audit_api_idempotency";
    private long ActorId() => currentUser.Id ?? throw new InvalidOperationException("认证用户缺少 ID");
    private static PaperQuery NormalizeQuery(PaperQuery query) => query with
    {
        Keyword = string.IsNullOrWhiteSpace(query.Keyword) ? null : query.Keyword.Trim(),
        PageIndex = Math.Max(1, query.PageIndex), PageSize = Math.Clamp(query.PageSize, 1, 100)
    };
    private static Result PermissionFailure() => Result.Fail("权限不足", "PERMISSION_DENIED");
    private static Result<T> PermissionFailure<T>() => Result<T>.Fail("权限不足", "PERMISSION_DENIED");
    private static Result ConcurrencyFailure() => Result.Fail("数据已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
    private static Result<T> ConcurrencyFailure<T>() => Result<T>.Fail("数据已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
