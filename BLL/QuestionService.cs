using ExamOps.BLL.Validators;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExamOps.BLL;

public sealed class QuestionService(
    IUnitOfWork uow,
    QuestionRepository questions,
    DepartmentRepository departments,
    DataScopeService scope,
    AuditRepository audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<IReadOnlyList<QuestionBankRow>> GetBanksAsync()
    {
        await currentUser.LoadAsync();
        RequireRead();
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.QuestionRead);
        return await questions.GetBanksAsync(allowed);
    }

    public async Task<QuestionBankRow?> GetBankAsync(long id)
    {
        await currentUser.LoadAsync();
        RequireRead();
        var bank = await questions.GetBankAsync(id);
        return bank is not null && await CanReadBankAsync(bank) ? bank : null;
    }

    public async Task<Result<long>> CreateBankAsync(string name, long departmentId, bool isShared)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionCreate)) return PermissionFailure<long>();
        name = name.Trim();
        if (name.Length is 0 or > 200) return Result<long>.Fail("题库名称需为 1–200 个字符", "INVALID_NAME");
        if (!(await scope.EnsureDepartmentAccessAsync(departmentId, Permissions.QuestionCreate)).IsSuccess)
            return Result<long>.Fail("无权在该部门创建题库", "OUT_OF_SCOPE");
        var now = clock.UtcNow;
        var actorId = ActorId();
        long? createdBankId = null;
        try
        {
            // Shared context + explicit transaction keeps the bank and its audit event atomic.
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var bank = new QuestionBank
                {
                    Name = name, DepartmentId = departmentId, IsShared = isShared, Status = "Active",
                    CreatedBy = actorId, CreatedAt = now
                };
                await questions.AddBankAsync(db, bank);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.bank.create", "QuestionBank", bank.Id,
                    after: new { bank.Name, bank.DepartmentId, bank.IsShared, bank.Status });
                await db.SaveChangesAsync();
                createdBankId = bank.Id;
            });
            return Result<long>.Ok(createdBankId ?? throw new InvalidOperationException("题库创建结果丢失。"));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Result<long>.Fail("题库数据已存在，请重试", "DUPLICATE_BANK");
        }
    }

    public async Task<Result> UpdateBankAsync(long id, string name, bool isShared, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionUpdate)) return PermissionFailure();
        var row = await questions.GetBankAsync(id);
        if (row is null) return Result.Fail("题库不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(row.DepartmentId, Permissions.QuestionUpdate)).IsSuccess)
            return Result.Fail("无权修改该题库", "OUT_OF_SCOPE");
        name = name.Trim();
        if (name.Length is 0 or > 200) return Result.Fail("题库名称需为 1–200 个字符", "INVALID_NAME");
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var bank = await questions.GetBankTrackedAsync(db, id);
                if (bank is null) { outcome = Result.Fail("题库不存在", "NOT_FOUND"); return; }
                if (bank.RowVersion != expectedRowVersion) { outcome = ConcurrencyFailure(); return; }
                if (bank.Status != "Active") { outcome = Result.Fail("已归档题库不能修改", "BANK_ARCHIVED"); return; }
                var before = new { bank.Name, bank.IsShared, bank.Status };
                bank.Name = name;
                bank.IsShared = isShared;
                bank.UpdatedAt = now;
                bank.RowVersion++;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.bank.update", "QuestionBank", id, before,
                    new { bank.Name, bank.IsShared, bank.Status });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure();
        }
    }

    public async Task<Result> ArchiveBankAsync(long id, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionUpdate)) return PermissionFailure();
        var row = await questions.GetBankAsync(id);
        if (row is null) return Result.Fail("题库不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(row.DepartmentId, Permissions.QuestionUpdate)).IsSuccess)
            return Result.Fail("无权归档该题库", "OUT_OF_SCOPE");
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var bank = await questions.GetBankTrackedAsync(db, id);
                if (bank is null) { outcome = Result.Fail("题库不存在", "NOT_FOUND"); return; }
                if (bank.RowVersion != expectedRowVersion) { outcome = ConcurrencyFailure(); return; }
                if (bank.Status == "Archived") { outcome = Result.Fail("题库已归档", "ALREADY_ARCHIVED"); return; }
                bank.Status = "Archived";
                bank.UpdatedAt = now;
                bank.RowVersion++;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.bank.archive", "QuestionBank", id,
                    after: new { bank.Status });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure();
        }
    }

    public async Task<Result> SetBankActiveAsync(long id, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionUpdate)) return PermissionFailure();
        var row = await questions.GetBankAsync(id);
        if (row is null) return Result.Fail("题库不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(row.DepartmentId, Permissions.QuestionUpdate)).IsSuccess)
            return Result.Fail("无权修改该题库", "OUT_OF_SCOPE");
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var bank = await questions.GetBankTrackedAsync(db, id);
                if (bank is null) { outcome = Result.Fail("题库不存在", "NOT_FOUND"); return; }
                if (bank.RowVersion != expectedRowVersion) { outcome = ConcurrencyFailure(); return; }
                if (bank.Status == "Active") { outcome = Result.Fail("题库已启用", "ALREADY_ACTIVE"); return; }
                bank.Status = "Active";
                bank.UpdatedAt = now;
                bank.RowVersion++;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.bank.enable", "QuestionBank", id,
                    after: new { bank.Status });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure();
        }
    }

    public async Task<IReadOnlyList<QuestionCategoryRow>> GetCategoriesAsync(long bankId)
    {
        await currentUser.LoadAsync();
        RequireRead();
        var bank = await questions.GetBankAsync(bankId);
        if (bank is null || !await CanReadBankAsync(bank)) return [];
        return await questions.GetCategoriesAsync(bankId);
    }

    public async Task<Result<long>> CreateCategoryAsync(long bankId, string name, long? parentId)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionUpdate)) return PermissionFailure<long>();
        var bank = await questions.GetBankAsync(bankId);
        if (bank is null) return Result<long>.Fail("题库不存在", "NOT_FOUND");
        if (!await EnsureWritableBankAsync(bank, Permissions.QuestionUpdate)) return Result<long>.Fail("无权修改该题库或题库已归档", "OUT_OF_SCOPE");
        name = name.Trim();
        if (name.Length is 0 or > 128) return Result<long>.Fail("分类名称需为 1–128 个字符", "INVALID_NAME");
        if (parentId.HasValue)
        {
            var parent = await questions.GetCategoryRowAsync(parentId.Value);
            if (parent is null || parent.BankId != bankId || !parent.IsActive)
                return Result<long>.Fail("上级分类不存在、已停用或不属于该题库", "PARENT_NOT_FOUND");
        }
        long? createdCategoryId = null;
        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var category = new QuestionCategory { BankId = bankId, ParentId = parentId, Name = name, IsActive = true, CreatedAt = now };
                await questions.AddCategoryAsync(db, category);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.category.create", "QuestionCategory", category.Id,
                    after: new { category.BankId, category.ParentId, category.Name });
                await db.SaveChangesAsync();
                createdCategoryId = category.Id;
            });
            return Result<long>.Ok(createdCategoryId ?? throw new InvalidOperationException("分类创建结果丢失。"));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Result<long>.Fail("同一上级下已存在该分类名称", "DUPLICATE_CATEGORY");
        }
    }

    public async Task<Result> SetCategoryActiveAsync(long id, bool isActive, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionUpdate)) return PermissionFailure();
        var row = await questions.GetCategoryRowAsync(id);
        if (row is null) return Result.Fail("分类不存在", "NOT_FOUND");
        var bank = await questions.GetBankAsync(row.BankId);
        if (bank is null || !await EnsureWritableBankAsync(bank, Permissions.QuestionUpdate)) return Result.Fail("无权修改该题库或题库已归档", "OUT_OF_SCOPE");
        if (!isActive && (await questions.GetCategoriesAsync(row.BankId)).Any(x => x.ParentId == id && x.IsActive))
            return Result.Fail("请先停用全部子分类", "HAS_ACTIVE_CHILD");
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var category = await questions.GetCategoryTrackedAsync(db, id);
                if (category is null) { outcome = Result.Fail("分类不存在", "NOT_FOUND"); return; }
                if (category.RowVersion != expectedRowVersion) { outcome = ConcurrencyFailure(); return; }
                await questions.SetCategoryActiveAsync(db, id, isActive, now);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), isActive ? "question.category.enable" : "question.category.disable",
                    "QuestionCategory", id, after: new { category.Name, category.IsActive });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure();
        }
    }

    public async Task<IReadOnlyList<QuestionTagRow>> GetTagsAsync()
    {
        await currentUser.LoadAsync();
        RequireRead();
        return await questions.GetTagsAsync();
    }

    public async Task<Result<QuestionTagRow>> CreateTagAsync(string name)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionUpdate)) return PermissionFailure<QuestionTagRow>();
        name = name.Trim();
        if (name.Length is 0 or > 128) return Result<QuestionTagRow>.Fail("标签名称需为 1–128 个字符", "INVALID_NAME");
        if (await questions.ExistsTagNameAsync(name)) return Result<QuestionTagRow>.Fail("标签已存在", "DUPLICATE_TAG");
        QuestionTag? tag = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                tag = await questions.AddTagAsync(db, name);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.tag.create", "QuestionTag",
                    tag.Id, after: new { Name = name });
                await db.SaveChangesAsync();
            });
            return Result<QuestionTagRow>.Ok(new(tag!.Id, tag.Name, tag.IsActive));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Result<QuestionTagRow>.Fail("标签已存在", "DUPLICATE_TAG");
        }
    }

    public async Task<PagedResult<QuestionRow>> GetPagedAsync(QuestionQuery query)
    {
        await currentUser.LoadAsync();
        RequireRead();
        query = query with
        {
            PageIndex = Math.Max(1, query.PageIndex), PageSize = Math.Clamp(query.PageSize, 1, 100),
            Type = string.IsNullOrWhiteSpace(query.Type) || query.Type is "SingleChoice" or "MultipleChoice" or "TrueFalse" ? query.Type : "Invalid",
            Status = string.IsNullOrWhiteSpace(query.Status) || query.Status is "Draft" or "Published" or "Archived" ? query.Status : "Invalid"
        };
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.QuestionRead);
        return await questions.GetPagedAsync(query, allowed);
    }

    public async Task<QuestionDetail?> GetDetailAsync(long id, long? versionId = null)
    {
        await currentUser.LoadAsync();
        RequireRead();
        var detail = await questions.GetDetailAsync(uow.Db, id, versionId);
        if (detail is null) return null;
        var bank = await questions.GetBankAsync(detail.BankId);
        if (bank is null || !await CanReadBankAsync(bank)) return null;
        if (await CanSeeAnswersAsync(bank)) return detail;
        return detail with
        {
            Options = detail.Options.Select(x => x with { IsCorrect = false }).ToArray(),
            AnswerValue = "", AnswerValues = [], TrueFalseAnswer = false, Analysis = null
        };
    }

    public async Task<Result<CreateQuestionResult>> CreateAsync(QuestionEditInput input)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionCreate)) return Result<CreateQuestionResult>.Fail("无权创建题目", "PERMISSION_DENIED");
        var validation = QuestionValidator.Validate(input);
        if (!validation.IsSuccess) return Result<CreateQuestionResult>.Fail(validation.Error!, validation.Code);
        var content = validation.Value!;
        var bank = await questions.GetBankAsync(input.BankId);
        if (bank is null) return Result<CreateQuestionResult>.Fail("题库不存在", "NOT_FOUND");
        if (!await EnsureWritableBankAsync(bank, Permissions.QuestionCreate)) return Result<CreateQuestionResult>.Fail("无权在该题库创建题目或题库已归档", "OUT_OF_SCOPE");
        if (await questions.ExistsExternalCodeAsync(uow.Db, bank.Id, content.ExternalCode, null))
            return Result<CreateQuestionResult>.Fail("题库内外部编码已存在", "DUPLICATE_EXTERNAL_CODE");
        var now = clock.UtcNow;
        Result<CreateQuestionResult>? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var trackedBank = await questions.GetBankTrackedAsync(db, bank.Id);
                if (trackedBank is null || trackedBank.Status != "Active") { outcome = Result<CreateQuestionResult>.Fail("题库不存在或已归档", "BANK_ARCHIVED"); return; }
                if (await questions.ExistsExternalCodeAsync(db, bank.Id, content.ExternalCode, null))
                { outcome = Result<CreateQuestionResult>.Fail("题库内外部编码已存在", "DUPLICATE_EXTERNAL_CODE"); return; }
                if (!await questions.CategoriesBelongToBankAsync(db, bank.Id, content.CategoryIds))
                { outcome = Result<CreateQuestionResult>.Fail("分类不存在、已停用或不属于该题库", "INVALID_CATEGORY"); return; }
                var question = new Question
                {
                    BankId = bank.Id, ExternalCode = content.ExternalCode, Type = content.Type, Status = "Draft", CreatedAt = now
                };
                var version = new QuestionVersion
                {
                    VersionNo = 1, Stem = content.Stem, AnswerJson = content.AnswerJson, Difficulty = content.Difficulty,
                    DefaultScore = content.DefaultScore, Analysis = content.Analysis, Status = "Draft", Source = "Manual", CreatedAt = now
                };
                var tags = await questions.EnsureTagsAsync(db, content.TagNames, now);
                await questions.AddQuestionAsync(db, question, version, ToEntities(content.Options), tags, ToCategories(content.CategoryIds));
                await db.SaveChangesAsync();
                question.CurrentVersionId = version.Id;
                question.UpdatedAt = now;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.create", "Question", question.Id,
                    after: new { question.BankId, question.ExternalCode, question.Type, VersionId = version.Id });
                await db.SaveChangesAsync();
                outcome = Result<CreateQuestionResult>.Ok(new(question.Id, version.Id));
            });
            return outcome!;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return UniqueMessage<CreateQuestionResult>(ex, "题库内外部编码已存在", "DUPLICATE_EXTERNAL_CODE");
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure<CreateQuestionResult>();
        }
    }

    public async Task<Result> UpdateAsync(long id, QuestionEditInput input, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionUpdate)) return PermissionFailure();
        var existing = await questions.GetDetailAsync(uow.Db, id);
        if (existing is null) return Result.Fail("题目不存在", "NOT_FOUND");
        var validation = QuestionValidator.Validate(input);
        if (!validation.IsSuccess) return Result.Fail(validation.Error!, validation.Code);
        var content = validation.Value!;
        if (content.BankId != existing.BankId) return Result.Fail("不能移动题目所属题库", "BANK_IMMUTABLE");
        if (existing.Status == "Published" && content.Type != existing.Type)
            return Result.Fail("已发布题目不能修改题型", "TYPE_IMMUTABLE");
        var bank = await questions.GetBankAsync(existing.BankId);
        if (bank is null || !await EnsureWritableBankAsync(bank, Permissions.QuestionUpdate)) return Result.Fail("无权修改该题库或题库已归档", "OUT_OF_SCOPE");
        if (existing.Status == "Archived") return Result.Fail("已归档题目不能修改", "QUESTION_ARCHIVED");
        if (existing.DraftVersionId == 0) return Result.Fail("题目没有草稿版本", "DRAFT_NOT_FOUND");
        if (await questions.ExistsExternalCodeAsync(uow.Db, bank.Id, content.ExternalCode, id))
            return Result.Fail("题库内外部编码已存在", "DUPLICATE_EXTERNAL_CODE");
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var question = await questions.GetQuestionTrackedAsync(db, id);
                if (question is null) { outcome = Result.Fail("题目不存在", "NOT_FOUND"); return; }
                if (question.RowVersion != expectedRowVersion) { outcome = ConcurrencyFailure(); return; }
                if (question.Status == "Archived") { outcome = Result.Fail("已归档题目不能修改", "QUESTION_ARCHIVED"); return; }
                if (question.Status == "Published" && content.Type != question.Type)
                { outcome = Result.Fail("已发布题目不能修改题型", "TYPE_IMMUTABLE"); return; }
                var version = await questions.GetVersionTrackedAsync(db, existing.DraftVersionId);
                if (version is null || version.QuestionId != id || version.Status != "Draft") { outcome = Result.Fail("草稿版本不存在", "DRAFT_NOT_FOUND"); return; }
                if (!await questions.CategoriesBelongToBankAsync(db, question.BankId, content.CategoryIds))
                { outcome = Result.Fail("分类不存在、已停用或不属于该题库", "INVALID_CATEGORY"); return; }
                if (await questions.ExistsExternalCodeAsync(db, question.BankId, content.ExternalCode, id))
                { outcome = Result.Fail("题库内外部编码已存在", "DUPLICATE_EXTERNAL_CODE"); return; }
                var before = new { question.ExternalCode, question.Type, version.Stem, version.Difficulty, version.DefaultScore };
                question.ExternalCode = content.ExternalCode;
                question.Type = content.Type;
                if (question.Status == "Draft") question.CurrentVersionId = version.Id;
                await questions.UpdateDraftAsync(db, question, version, content.Stem, content.AnswerJson,
                    content.Difficulty, content.DefaultScore, content.Analysis,
                    ToEntities(content.Options), content.TagNames, content.CategoryIds, now);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.update", "Question", id, before,
                    new { question.ExternalCode, question.Type, version.Stem, version.Difficulty, version.DefaultScore, VersionId = version.Id });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return UniqueMessage(ex, "题库内外部编码已存在", "DUPLICATE_EXTERNAL_CODE");
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure();
        }
    }

    public async Task<Result<CreateQuestionResult>> CreateDraftVersionAsync(long id, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionUpdate)) return Result<CreateQuestionResult>.Fail("无权创建题目草稿", "PERMISSION_DENIED");
        var existing = await questions.GetDetailAsync(uow.Db, id);
        if (existing is null) return Result<CreateQuestionResult>.Fail("题目不存在", "NOT_FOUND");
        if (existing.Status is not ("Draft" or "Published")) return Result<CreateQuestionResult>.Fail("已归档题目不能创建草稿", "QUESTION_ARCHIVED");
        var bank = await questions.GetBankAsync(existing.BankId);
        if (bank is null || !await EnsureWritableBankAsync(bank, Permissions.QuestionUpdate)) return Result<CreateQuestionResult>.Fail("无权修改该题库或题库已归档", "OUT_OF_SCOPE");
        var now = clock.UtcNow;
        Result<CreateQuestionResult>? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var question = await questions.GetQuestionTrackedAsync(db, id);
                if (question is null) { outcome = Result<CreateQuestionResult>.Fail("题目不存在", "NOT_FOUND"); return; }
                if (question.RowVersion != expectedRowVersion) { outcome = ConcurrencyFailure<CreateQuestionResult>(); return; }
                if (await questions.GetDraftVersionIdAsync(db, id) is not null)
                { outcome = Result<CreateQuestionResult>.Fail("题目已有草稿版本", "DRAFT_EXISTS"); return; }
                var version = await questions.CopyDraftAsync(db, id, now);
                if (version is null) { outcome = Result<CreateQuestionResult>.Fail("没有可复制的题目版本", "VERSION_NOT_FOUND"); return; }
                await db.SaveChangesAsync();
                if (question.Status == "Draft") question.CurrentVersionId = version.Id;
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.draft.create", "Question", id,
                    after: new { VersionId = version.Id, version.VersionNo });
                await db.SaveChangesAsync();
                outcome = Result<CreateQuestionResult>.Ok(new(id, version.Id));
            });
            return outcome!;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Result<CreateQuestionResult>.Fail("版本号冲突，请刷新后重试", "VERSION_CONFLICT");
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure<CreateQuestionResult>();
        }
    }

    public async Task<Result> PublishAsync(long id, long versionId, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionPublish)) return PermissionFailure();
        var existing = await questions.GetDetailAsync(uow.Db, id);
        if (existing is null) return Result.Fail("题目不存在", "NOT_FOUND");
        if (!existing.Versions.Any(x => x.Id == versionId && x.Status == "Draft")) return Result.Fail("只能发布草稿版本", "DRAFT_REQUIRED");
        var bank = await questions.GetBankAsync(existing.BankId);
        if (bank is null || !await EnsureWritableBankAsync(bank, Permissions.QuestionPublish)) return Result.Fail("无权发布该题库题目或题库已归档", "OUT_OF_SCOPE");
        var draft = await questions.GetDraftInputAsync(uow.Db, versionId);
        if (draft is null) return Result.Fail("草稿版本不存在", "NOT_FOUND");
        var validation = QuestionValidator.Validate(draft);
        if (!validation.IsSuccess) return Result.Fail(validation.Error!, validation.Code);
        if (!await questions.CategoriesBelongToBankAsync(uow.Db, bank.Id, validation.Value!.CategoryIds))
            return Result.Fail("分类已停用或不属于该题库", "INVALID_CATEGORY");
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var question = await questions.GetQuestionTrackedAsync(db, id);
                var version = await questions.GetVersionTrackedAsync(db, versionId);
                if (question is null || version is null) { outcome = Result.Fail("题目或版本不存在", "NOT_FOUND"); return; }
                if (question.RowVersion != expectedRowVersion) { outcome = ConcurrencyFailure(); return; }
                if (!await questions.PublishAsync(db, id, versionId, now, ActorId()))
                { outcome = Result.Fail("只能发布未归档题目的草稿版本", "INVALID_STATE"); return; }
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.publish", "Question", id,
                    after: new { VersionId = versionId, question.Status });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        {
            return Result.Fail("选项与标准答案不一致，无法发布", "INVALID_ANSWER");
        }
    }

    public async Task<Result> ArchiveAsync(long id, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!RequirePermission(Permissions.QuestionUpdate)) return PermissionFailure();
        var existing = await questions.GetDetailAsync(uow.Db, id);
        if (existing is null) return Result.Fail("题目不存在", "NOT_FOUND");
        var bank = await questions.GetBankAsync(existing.BankId);
        if (bank is null || !await EnsureWritableBankAsync(bank, Permissions.QuestionUpdate)) return Result.Fail("无权归档该题库题目或题库已归档", "OUT_OF_SCOPE");
        var now = clock.UtcNow;
        Result? outcome = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                var question = await questions.GetQuestionTrackedAsync(db, id);
                if (question is null) { outcome = Result.Fail("题目不存在", "NOT_FOUND"); return; }
                if (question.RowVersion != expectedRowVersion) { outcome = ConcurrencyFailure(); return; }
                if (!await questions.ArchiveAsync(db, id, now)) { outcome = Result.Fail("题目已归档", "ALREADY_ARCHIVED"); return; }
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.archive", "Question", id,
                    after: new { question.Status, question.ArchivedAt });
                await db.SaveChangesAsync();
                outcome = Result.Ok();
            });
            return outcome!;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure();
        }
    }

    private async Task<bool> CanReadBankAsync(QuestionBankRow bank)
    {
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.QuestionRead);
        if (allowed is null || allowed.Contains(bank.DepartmentId)) return true;
        if (!bank.IsShared) return false;
        var owner = await departments.GetByIdAsync(bank.DepartmentId);
        if (owner is null) return false;
        var all = await departments.GetAllAsync();
        return all.Any(x => allowed.Contains(x.Id) && x.Path.StartsWith(owner.Path, StringComparison.Ordinal));
    }

    private async Task<bool> CanSeeAnswersAsync(QuestionBankRow bank)
    {
        foreach (var permission in new[] { Permissions.QuestionUpdate, Permissions.QuestionPublish, Permissions.QuestionImport })
        {
            if (currentUser.HasPermission(permission)
                && (await scope.EnsureDepartmentAccessAsync(bank.DepartmentId, permission)).IsSuccess)
                return true;
        }
        return false;
    }

    private async Task<bool> EnsureWritableBankAsync(QuestionBankRow bank, string permission)
        => bank.Status == "Active" && (await scope.EnsureDepartmentAccessAsync(bank.DepartmentId, permission)).IsSuccess;

    private void RequireRead()
    {
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.QuestionRead))
            throw new UnauthorizedAccessException("无权查看题库与题目");
    }

    private bool RequirePermission(string permission)
        => currentUser.IsAuthenticated && currentUser.HasPermission(permission);

    private long ActorId() => currentUser.Id ?? throw new InvalidOperationException("认证用户缺少 ID");

    private static Result<T> PermissionFailure<T>() => Result<T>.Fail("权限不足", "PERMISSION_DENIED");
    private static Result PermissionFailure() => Result.Fail("权限不足", "PERMISSION_DENIED");
    private static Result ConcurrencyFailure() => Result.Fail("数据已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
    private static Result<T> ConcurrencyFailure<T>() => Result<T>.Fail("数据已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static Result UniqueMessage(DbUpdateException exception, string fallback, string code)
    {
        var constraint = (exception.InnerException as PostgresException)?.ConstraintName;
        return constraint == "ux_question_tag_name"
            ? Result.Fail("标签已存在", "DUPLICATE_TAG")
            : Result.Fail(fallback, code);
    }

    private static Result<T> UniqueMessage<T>(DbUpdateException exception, string fallback, string code)
    {
        var constraint = (exception.InnerException as PostgresException)?.ConstraintName;
        return constraint == "ux_question_tag_name"
            ? Result<T>.Fail("标签已存在", "DUPLICATE_TAG")
            : Result<T>.Fail(fallback, code);
    }

    private static IReadOnlyList<QuestionOption> ToEntities(IReadOnlyList<QuestionOptionInput> options)
        => options.Select(x => new QuestionOption { Label = x.Label, Content = x.Content, IsCorrect = x.IsCorrect, SortOrder = x.SortOrder }).ToArray();

    private static IReadOnlyList<QuestionCategoryLink> ToCategories(IReadOnlyList<long> ids)
        => ids.Distinct().Select(x => new QuestionCategoryLink { CategoryId = x }).ToArray();
}
