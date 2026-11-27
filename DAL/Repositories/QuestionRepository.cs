using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class QuestionRepository(IUnitOfWork uow)
{
    private ExamDbContext Db => uow.Db;

    public async Task<IReadOnlyList<QuestionBankRow>> GetBanksAsync(IReadOnlyList<long>? allowedDepartmentIds)
    {
        var db = Db;
        var banks = VisibleBanks(db, allowedDepartmentIds);
        return await banks.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new QuestionBankRow(
                x.Id, x.Name, x.DepartmentId,
                db.Departments.Where(d => d.Id == x.DepartmentId).Select(d => d.Name).First(),
                x.IsShared, x.Status, db.Questions.Count(q => q.BankId == x.Id), x.RowVersion))
            .ToListAsync();
    }

    public async Task<QuestionBankRow?> GetBankAsync(long id)
    {
        var db = Db;
        return await db.QuestionBanks.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new QuestionBankRow(
                x.Id, x.Name, x.DepartmentId,
                db.Departments.Where(d => d.Id == x.DepartmentId).Select(d => d.Name).First(),
                x.IsShared, x.Status, db.Questions.Count(q => q.BankId == x.Id), x.RowVersion))
            .FirstOrDefaultAsync();
    }

    public async Task<QuestionBank?> GetBankTrackedAsync(ExamDbContext db, long id)
    {
        return await db.QuestionBanks.FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<QuestionRow>> GetPagedAsync(QuestionQuery query, IReadOnlyList<long>? allowedDepartmentIds)
    {
        var db = Db;
        var allowed = allowedDepartmentIds?.ToArray();
        var banks = VisibleBanks(db, allowed);
        var questions = from q in db.Questions.AsNoTracking()
                        join b in banks on q.BankId equals b.Id
                        select new { Question = q, b.Name };
        if (query.BankId.HasValue) questions = questions.Where(x => x.Question.BankId == query.BankId.Value);
        if (!string.IsNullOrWhiteSpace(query.Type)) questions = questions.Where(x => x.Question.Type == query.Type);
        if (!string.IsNullOrWhiteSpace(query.Status)) questions = questions.Where(x => x.Question.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var pattern = $"%{EscapeLike(query.Keyword.Trim())}%";
            questions = questions.Where(x => EF.Functions.ILike(x.Question.ExternalCode ?? "", pattern)
                || EF.Functions.ILike(db.QuestionVersions.Where(v => v.QuestionId == x.Question.Id)
                    .OrderByDescending(v => v.VersionNo).Select(v => v.Stem).FirstOrDefault() ?? "", pattern));
        }
        if (query.TagId.HasValue)
        {
            var tagId = query.TagId.Value;
            questions = questions.Where(x => db.QuestionTagLinks.Any(link => link.TagId == tagId
                && link.QuestionVersionId == (x.Question.CurrentVersionId ?? db.QuestionVersions.Where(v => v.QuestionId == x.Question.Id)
                    .OrderByDescending(v => v.VersionNo).Select(v => (long?)v.Id).FirstOrDefault())));
        }
        if (query.CategoryId.HasValue)
        {
            var categoryId = query.CategoryId.Value;
            questions = questions.Where(x => db.QuestionCategoryLinks.Any(link => link.CategoryId == categoryId
                && link.QuestionVersionId == (x.Question.CurrentVersionId ?? db.QuestionVersions.Where(v => v.QuestionId == x.Question.Id)
                    .OrderByDescending(v => v.VersionNo).Select(v => (long?)v.Id).FirstOrDefault())));
        }

        var total = await questions.CountAsync();
        var page = await questions.OrderBy(x => x.Question.Id)
            .Skip((query.PageIndex - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new QuestionPageItem(
                x.Question.Id, x.Question.BankId, x.Name, x.Question.ExternalCode, x.Question.Type, x.Question.Status, x.Question.RowVersion,
                x.Question.CurrentVersionId ?? db.QuestionVersions.Where(v => v.QuestionId == x.Question.Id)
                    .OrderByDescending(v => v.VersionNo).Select(v => (long?)v.Id).FirstOrDefault()))
            .ToListAsync();
        var versionIds = page.Where(x => x.VersionId.HasValue).Select(x => x.VersionId!.Value).Distinct().ToArray();
        var versions = await db.QuestionVersions.AsNoTracking().Where(x => versionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Stem, x.Difficulty, x.DefaultScore }).ToDictionaryAsync(x => x.Id);
        var links = await db.QuestionTagLinks.AsNoTracking().Where(x => versionIds.Contains(x.QuestionVersionId))
            .Select(x => new { x.QuestionVersionId, TagName = db.QuestionTags.Where(t => t.Id == x.TagId).Select(t => t.Name).First() })
            .ToListAsync();
        var tags = links.GroupBy(x => x.QuestionVersionId)
            .ToDictionary(x => x.Key, x => string.Join("、", x.Select(v => v.TagName).OrderBy(v => v, StringComparer.Ordinal)));
        var rows = page.Select(x =>
        {
            var version = x.VersionId.HasValue ? versions.GetValueOrDefault(x.VersionId.Value) : null;
            return new QuestionRow(x.Id, x.BankId, x.BankName,
                x.ExternalCode, x.Type, x.Status, version?.Stem ?? "", version?.Difficulty ?? "Medium",
                version?.DefaultScore ?? 1, x.VersionId.HasValue ? tags.GetValueOrDefault(x.VersionId.Value, "") : "", x.RowVersion);
        }).ToList();
        return new PagedResult<QuestionRow>(rows, total, query.PageIndex, query.PageSize);
    }

    public async Task<QuestionDetail?> GetDetailAsync(ExamDbContext db, long id, long? requestedVersionId = null)
    {
        var question = await db.Questions.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.BankId, x.ExternalCode, x.Type, x.Status, x.RowVersion, x.CurrentVersionId })
            .FirstOrDefaultAsync();
        if (question is null) return null;
        var versions = await db.QuestionVersions.AsNoTracking().Where(x => x.QuestionId == id)
            .OrderByDescending(x => x.VersionNo).ThenByDescending(x => x.Id)
            .Select(x => new QuestionVersionRow(x.Id, x.VersionNo, x.Status, x.PublishedAt, x.Id == question.CurrentVersionId))
            .ToListAsync();
        var draftVersionId = versions.FirstOrDefault(x => x.Status == "Draft" && question.Status != "Archived")?.Id;
        var versionId = requestedVersionId ?? draftVersionId ?? question.CurrentVersionId ?? versions.FirstOrDefault()?.Id;
        if (requestedVersionId.HasValue && versions.All(x => x.Id != requestedVersionId.Value)) return null;
        var version = versionId.HasValue
            ? await db.QuestionVersions.AsNoTracking().Where(x => x.Id == versionId.Value)
                .Select(x => new { x.Stem, x.Difficulty, x.DefaultScore, x.Analysis, x.AnswerJson, x.Status })
                .FirstOrDefaultAsync()
            : null;
        var options = versionId.HasValue
            ? await db.QuestionOptions.AsNoTracking().Where(x => x.QuestionVersionId == versionId.Value)
                .OrderBy(x => x.SortOrder).Select(x => new QuestionOptionRow(x.Label, x.Content, x.IsCorrect, x.SortOrder)).ToListAsync()
            : [];
        var tagNames = versionId.HasValue
            ? await db.QuestionTagLinks.AsNoTracking().Where(x => x.QuestionVersionId == versionId.Value)
                .OrderBy(x => x.TagId).Select(x => db.QuestionTags.Where(t => t.Id == x.TagId).Select(t => t.Name).First()).ToListAsync()
            : [];
        var categoryIds = versionId.HasValue
            ? await db.QuestionCategoryLinks.AsNoTracking().Where(x => x.QuestionVersionId == versionId.Value)
                .OrderBy(x => x.CategoryId).Select(x => x.CategoryId).ToListAsync()
            : [];
        var answer = version is null ? QuestionAnswerValues.Empty : QuestionAnswerJson.Read(version.AnswerJson, question.Type);
        return new QuestionDetail(question.Id, question.BankId, question.ExternalCode, question.Type, question.Status,
            question.RowVersion, question.CurrentVersionId, versions,
            versions.FirstOrDefault(x => x.Status == "Draft" && question.Status != "Archived")?.Id ?? 0,
            version?.Stem ?? "", version?.Difficulty ?? "Medium", version?.DefaultScore ?? 1, version?.Analysis,
            options, answer.Value, answer.Values, answer.TrueFalseAnswer, tagNames, categoryIds);
    }

    public async Task<Question?> GetQuestionTrackedAsync(ExamDbContext db, long id)
    {
        return await db.Questions.FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<QuestionVersion?> GetVersionTrackedAsync(ExamDbContext db, long id)
    {
        return await db.QuestionVersions.FirstOrDefaultAsync(x => x.Id == id);
    }

    public Task AddBankAsync(ExamDbContext db, QuestionBank bank)
    {
        db.QuestionBanks.Add(bank);
        return Task.CompletedTask;
    }

    public async Task AddQuestionAsync(ExamDbContext db, Question question, QuestionVersion version,
        IReadOnlyList<QuestionOption> options, IReadOnlyList<QuestionTag> tags, IReadOnlyList<QuestionCategoryLink> categories)
    {
        // 显式事务内先取得身份键，再把同一版本的子行写到真实外键上。
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        version.QuestionId = question.Id;
        db.QuestionVersions.Add(version);
        await db.SaveChangesAsync();
        var versionId = version.Id;
        db.QuestionOptions.AddRange(options.Select(x => new QuestionOption
        {
            QuestionVersionId = versionId, Label = x.Label, Content = x.Content, IsCorrect = x.IsCorrect, SortOrder = x.SortOrder
        }));
        db.QuestionTagLinks.AddRange(tags.Select(x => new QuestionTagLink { QuestionVersionId = versionId, TagId = x.Id, CreatedAt = version.CreatedAt }));
        db.QuestionCategoryLinks.AddRange(categories.Select(x => new QuestionCategoryLink
        {
            QuestionVersionId = versionId, CategoryId = x.CategoryId, CreatedAt = version.CreatedAt
        }));
    }

    public async Task UpdateDraftAsync(ExamDbContext db, Question question, QuestionVersion version,
        string stem, string answerJson, string difficulty, decimal defaultScore, string? analysis,
        IReadOnlyList<QuestionOption> options, IReadOnlyList<string> tagNames, IReadOnlyList<long> categoryIds, DateTime now)
    {
        if (version.Status != "Draft") throw new InvalidOperationException("只有草稿版本可以修改。");
        question.UpdatedAt = now;
        question.RowVersion++;
        db.QuestionOptions.RemoveRange(db.QuestionOptions.Where(x => x.QuestionVersionId == version.Id));
        db.QuestionTagLinks.RemoveRange(db.QuestionTagLinks.Where(x => x.QuestionVersionId == version.Id));
        db.QuestionCategoryLinks.RemoveRange(db.QuestionCategoryLinks.Where(x => x.QuestionVersionId == version.Id));
        // 先落删除，避免同一 SaveChanges 中“删选项 → 改答案”的中间态被数据库校验拒绝。
        await db.SaveChangesAsync();
        version.Stem = stem.Trim();
        version.AnswerJson = answerJson;
        version.Difficulty = difficulty;
        version.DefaultScore = defaultScore;
        version.Analysis = analysis?.Trim();
        version.RowVersion++;
        db.QuestionOptions.AddRange(options.Select(x => new QuestionOption
        {
            QuestionVersionId = version.Id, Label = x.Label, Content = x.Content, IsCorrect = x.IsCorrect, SortOrder = x.SortOrder
        }));
        var tags = await EnsureTagsAsync(db, tagNames, now);
        db.QuestionTagLinks.AddRange(tags.Select(x => new QuestionTagLink { QuestionVersionId = version.Id, TagId = x.Id, CreatedAt = now }));
        db.QuestionCategoryLinks.AddRange(categoryIds.Distinct().Select(x => new QuestionCategoryLink
        {
            QuestionVersionId = version.Id, CategoryId = x, CreatedAt = now
        }));
    }

    public async Task<QuestionVersion?> CopyDraftAsync(ExamDbContext db, long id, DateTime now)
    {
        var question = await GetQuestionTrackedAsync(db, id);
        if (question is null) return null;
        var sourceId = question.CurrentVersionId ?? await db.QuestionVersions.AsNoTracking()
            .Where(x => x.QuestionId == id).OrderByDescending(x => x.VersionNo).Select(x => (long?)x.Id).FirstOrDefaultAsync();
        if (!sourceId.HasValue) return null;
        var source = await db.QuestionVersions.AsNoTracking().FirstAsync(x => x.Id == sourceId.Value);
        var version = new QuestionVersion
        {
            QuestionId = id, VersionNo = await GetNextVersionNoAsync(db, id), Stem = source.Stem,
            AnswerJson = source.AnswerJson, Difficulty = source.Difficulty, DefaultScore = source.DefaultScore,
            Analysis = source.Analysis, Status = "Draft", Source = "Manual", CreatedAt = now
        };
        db.QuestionVersions.Add(version);
        await db.SaveChangesAsync();
        db.QuestionOptions.AddRange(await db.QuestionOptions.AsNoTracking().Where(x => x.QuestionVersionId == source.Id)
            .OrderBy(x => x.SortOrder).Select(x => new QuestionOption
            {
                QuestionVersionId = version.Id, Label = x.Label, Content = x.Content, IsCorrect = x.IsCorrect, SortOrder = x.SortOrder
            }).ToListAsync());
        var tagIds = await db.QuestionTagLinks.AsNoTracking().Where(x => x.QuestionVersionId == source.Id).Select(x => x.TagId).ToListAsync();
        db.QuestionTagLinks.AddRange(tagIds.Select(x => new QuestionTagLink { QuestionVersionId = version.Id, TagId = x, CreatedAt = now }));
        var categoryIds = await db.QuestionCategoryLinks.AsNoTracking().Where(x => x.QuestionVersionId == source.Id).Select(x => x.CategoryId).ToListAsync();
        db.QuestionCategoryLinks.AddRange(categoryIds.Select(x => new QuestionCategoryLink
        {
            QuestionVersionId = version.Id, CategoryId = x, CreatedAt = now
        }));
        question.UpdatedAt = now;
        question.RowVersion++;
        return version;
    }

    public async Task<bool> PublishAsync(ExamDbContext db, long id, long versionId, DateTime now, long actorId)
    {
        var question = await GetQuestionTrackedAsync(db, id);
        var version = await GetVersionTrackedAsync(db, versionId);
        if (question is null || version is null || version.QuestionId != id || version.Status != "Draft" || question.Status == "Archived")
            return false;
        version.Status = "Published";
        version.PublishedAt = now;
        version.PublishedBy = actorId;
        version.RowVersion++;
        // 先提交版本状态，再切换题目指针；BEFORE 触发器要求 Published 指针必须已指向 Published 版本。
        await db.SaveChangesAsync();
        question.Status = "Published";
        question.CurrentVersionId = versionId;
        question.UpdatedAt = now;
        question.RowVersion++;
        return true;
    }

    public async Task<bool> ArchiveAsync(ExamDbContext db, long id, DateTime now)
    {
        var question = await GetQuestionTrackedAsync(db, id);
        if (question is null || question.Status == "Archived") return false;
        question.Status = "Archived";
        question.ArchivedAt = now;
        question.UpdatedAt = now;
        question.RowVersion++;
        return true;
    }

    public async Task<IReadOnlyList<QuestionCategoryRow>> GetCategoriesAsync(long bankId)
        => await Db.QuestionCategories.AsNoTracking().Where(x => x.BankId == bankId)
            .OrderBy(x => x.ParentId).ThenBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new QuestionCategoryRow(x.Id, x.BankId, x.ParentId, x.Name, x.IsActive, x.RowVersion)).ToListAsync();

    public async Task<QuestionCategory?> GetCategoryTrackedAsync(ExamDbContext db, long id)
    {
        return await db.QuestionCategories.FirstOrDefaultAsync(x => x.Id == id);
    }

    public Task AddCategoryAsync(ExamDbContext db, QuestionCategory category)
    {
        db.QuestionCategories.Add(category);
        return Task.CompletedTask;
    }

    public async Task<bool> SetCategoryActiveAsync(ExamDbContext db, long id, bool isActive, DateTime now)
    {
        var category = await GetCategoryTrackedAsync(db, id);
        if (category is null) return false;
        category.IsActive = isActive;
        category.UpdatedAt = now;
        category.RowVersion++;
        return true;
    }

    public async Task<IReadOnlyList<QuestionTagRow>> GetTagsAsync()
        => await Db.QuestionTags.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new QuestionTagRow(x.Id, x.Name, x.IsActive)).ToListAsync();

    public async Task<bool> ExistsTagNameAsync(string name)
        => await Db.QuestionTags.AsNoTracking().AnyAsync(x => x.Name == name);

    public async Task<QuestionTag> AddTagAsync(ExamDbContext db, string name)
    {
        var tag = new QuestionTag { Name = name, IsActive = true };
        db.QuestionTags.Add(tag);
        return await Task.FromResult(tag);
    }

    public async Task<IReadOnlyList<QuestionTag>> EnsureTagsAsync(ExamDbContext db, IReadOnlyList<string> names, DateTime now)
    {
        var wanted = names.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var tags = await db.QuestionTags.AsNoTracking().Where(x => x.IsActive).ToListAsync();
        var byName = tags.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<QuestionTag>();
        foreach (var name in wanted)
        {
            if (!byName.TryGetValue(name, out var tag))
            {
                tag = new QuestionTag { Name = name, IsActive = true, CreatedAt = now };
                db.QuestionTags.Add(tag);
                byName[name] = tag;
            }
            result.Add(tag);
        }
        return result;
    }

    public Task<bool> ExistsExternalCodeAsync(ExamDbContext db, long bankId, string? code, long? excludeQuestionId)
        => code is null ? Task.FromResult(false)
            : db.Questions.AsNoTracking().AnyAsync(x => x.BankId == bankId && x.ExternalCode == code && (excludeQuestionId == null || x.Id != excludeQuestionId));

    public async Task<int> GetNextVersionNoAsync(ExamDbContext db, long questionId)
        => await db.QuestionVersions.AsNoTracking().Where(x => x.QuestionId == questionId).Select(x => (int?)x.VersionNo).MaxAsync() + 1 ?? 1;

    public async Task<bool> CategoriesBelongToBankAsync(ExamDbContext db, long bankId, IReadOnlyList<long> categoryIds)
    {
        var ids = categoryIds.Distinct().ToArray();
        return ids.Length == 0 || await db.QuestionCategories.AsNoTracking()
            .Where(x => x.BankId == bankId && x.IsActive && ids.Contains(x.Id)).CountAsync() == ids.Length;
    }

    public async Task<QuestionCategoryRow?> GetCategoryRowAsync(long id)
        => await Db.QuestionCategories.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new QuestionCategoryRow(x.Id, x.BankId, x.ParentId, x.Name, x.IsActive, x.RowVersion)).FirstOrDefaultAsync();

    public async Task<QuestionEditInput?> GetDraftInputAsync(ExamDbContext db, long versionId)
    {
        var version = await db.QuestionVersions.AsNoTracking().Where(x => x.Id == versionId).FirstOrDefaultAsync();
        if (version is null) return null;
        var question = await db.Questions.AsNoTracking().Where(x => x.Id == version.QuestionId).FirstOrDefaultAsync();
        if (question is null) return null;
        var options = await db.QuestionOptions.AsNoTracking().Where(x => x.QuestionVersionId == versionId)
            .OrderBy(x => x.SortOrder).Select(x => new QuestionOptionInput(x.Label, x.Content, x.IsCorrect, x.SortOrder)).ToListAsync();
        var tags = await db.QuestionTagLinks.AsNoTracking().Where(x => x.QuestionVersionId == versionId)
            .Select(x => db.QuestionTags.Where(t => t.Id == x.TagId).Select(t => t.Name).First()).ToListAsync();
        var categories = await db.QuestionCategoryLinks.AsNoTracking().Where(x => x.QuestionVersionId == versionId)
            .Select(x => x.CategoryId).ToListAsync();
        var answer = QuestionAnswerJson.Read(version.AnswerJson, question.Type);
        return new QuestionEditInput(question.BankId, question.ExternalCode, question.Type, version.Stem,
            version.Difficulty, version.DefaultScore, version.Analysis, options, tags, categories,
            answer.Value, answer.Values, answer.TrueFalseAnswer);
    }

    public async Task<long?> GetDraftVersionIdAsync(ExamDbContext db, long questionId)
        => await db.QuestionVersions.AsNoTracking().Where(x => x.QuestionId == questionId && x.Status == "Draft")
            .OrderByDescending(x => x.VersionNo).Select(x => (long?)x.Id).FirstOrDefaultAsync();

    public async Task<PagedResult<PaperCandidateRow>> GetPaperCandidatesAsync(ExamDbContext db,
        IReadOnlyList<long>? allowedDepartmentIds, PaperCandidateQuery query)
    {
        var banks = VisibleBanks(db, allowedDepartmentIds);
        var candidates = from q in db.Questions.AsNoTracking()
                         join v in db.QuestionVersions.AsNoTracking() on q.CurrentVersionId equals v.Id
                         join b in banks on q.BankId equals b.Id
                         where q.Status == "Published" && v.Status == "Published"
                         select new { Question = q, Version = v, Bank = b };
        if (query.BankId.HasValue) candidates = candidates.Where(x => x.Question.BankId == query.BankId.Value);
        if (!string.IsNullOrWhiteSpace(query.QuestionType)) candidates = candidates.Where(x => x.Question.Type == query.QuestionType);
        if (!string.IsNullOrWhiteSpace(query.Difficulty)) candidates = candidates.Where(x => x.Version.Difficulty == query.Difficulty);
        if (query.CategoryId.HasValue)
        {
            var categoryIds = await DescendantCategoryIdsAsync(db, query.CategoryId.Value);
            candidates = candidates.Where(x => db.QuestionCategoryLinks.Any(link =>
                categoryIds.Contains(link.CategoryId) && link.QuestionVersionId == x.Version.Id));
        }
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var pattern = $"%{EscapeLike(query.Keyword.Trim())}%";
            candidates = candidates.Where(x => EF.Functions.ILike(x.Question.ExternalCode ?? "", pattern)
                || EF.Functions.ILike(x.Version.Stem, pattern));
        }
        var total = await candidates.CountAsync();
        var rows = await candidates.OrderBy(x => x.Question.Id).ThenBy(x => x.Version.Id)
            .Skip((query.PageIndex - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new PaperCandidateRow(x.Version.Id, x.Question.Id, x.Question.BankId,
                x.Bank.Name, x.Question.ExternalCode, x.Question.Type, x.Version.Stem,
                x.Version.Difficulty, x.Version.DefaultScore)).ToListAsync();
        return new PagedResult<PaperCandidateRow>(rows, total, query.PageIndex, query.PageSize);
    }

    public async Task<bool> PublishedPaperCandidatesExistAsync(ExamDbContext db,
        IReadOnlyList<long>? allowedDepartmentIds, IReadOnlyList<long> questionVersionIds)
    {
        var ids = questionVersionIds.Distinct().ToArray();
        if (ids.Length == 0) return true;
        var banks = VisibleBanks(db, allowedDepartmentIds);
        return await (from q in db.Questions.AsNoTracking()
                      join v in db.QuestionVersions.AsNoTracking() on q.CurrentVersionId equals v.Id
                      join b in banks on q.BankId equals b.Id
                      where q.Status == "Published" && v.Status == "Published" && ids.Contains(v.Id)
                      select v.Id).CountAsync() == ids.Length;
    }

    private static async Task<IReadOnlyList<long>> DescendantCategoryIdsAsync(ExamDbContext db, long categoryId)
    {
        var root = await db.QuestionCategories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == categoryId);
        if (root is null) return [];
        var categories = await db.QuestionCategories.AsNoTracking().Where(x => x.BankId == root.BankId)
            .Select(x => new { x.Id, x.ParentId }).ToListAsync();
        var children = categories.Where(x => x.ParentId.HasValue)
            .GroupBy(x => x.ParentId!.Value).ToDictionary(x => x.Key, x => x.Select(y => y.Id).ToArray());
        var ids = new HashSet<long> { root.Id };
        var queue = new Queue<long>([root.Id]);
        while (queue.TryDequeue(out var parentId))
        {
            if (!children.TryGetValue(parentId, out var childIds)) continue;
            foreach (var childId in childIds)
                if (ids.Add(childId)) queue.Enqueue(childId);
        }
        return ids.ToArray();
    }

    private static IQueryable<QuestionBank> VisibleBanks(ExamDbContext db, IReadOnlyList<long>? allowedDepartmentIds)
    {
        var banks = db.QuestionBanks.AsNoTracking();
        if (allowedDepartmentIds is null) return banks;
        var allowed = allowedDepartmentIds.ToArray();
        return banks.Where(x => allowed.Contains(x.DepartmentId) || (x.IsShared
            && db.Departments.Any(target => allowed.Contains(target.Id)
                && db.Departments.Any(owner => owner.Id == x.DepartmentId && target.Path.StartsWith(owner.Path)))));
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed record QuestionPageItem(long Id, long BankId, string BankName, string? ExternalCode, string Type, string Status,
        long RowVersion, long? VersionId);
}
