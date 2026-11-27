using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class PaperRepository(IUnitOfWork uow)
{
    private ExamDbContext Db => uow.Db;

    public async Task<PagedResult<PaperRow>> GetPagedAsync(PaperQuery query, IReadOnlyList<long>? allowedDepartmentIds)
    {
        var db = Db;
        var allowed = allowedDepartmentIds?.ToArray();
        var papers = from p in db.Papers.AsNoTracking()
                     join d in db.Departments.AsNoTracking() on p.DepartmentId equals d.Id
                     join v in db.PaperVersions.AsNoTracking() on p.CurrentVersionId equals v.Id into versions
                     from v in versions.DefaultIfEmpty()
                     where allowed == null || allowed.Contains(p.DepartmentId)
                     select new { Paper = p, Version = v, DepartmentName = d.Name };
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var pattern = $"%{EscapeLike(query.Keyword.Trim())}%";
            papers = papers.Where(x => EF.Functions.ILike(x.Paper.Title, pattern));
        }
        if (query.DepartmentId.HasValue) papers = papers.Where(x => x.Paper.DepartmentId == query.DepartmentId.Value);
        if (!string.IsNullOrWhiteSpace(query.Status)) papers = papers.Where(x => x.Paper.Status == query.Status);
        var total = await papers.CountAsync();
        var page = await papers.OrderBy(x => x.Paper.Id)
            .Skip((query.PageIndex - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        var versionIds = page.Where(x => x.Version != null).Select(x => x.Version!.Id).ToArray();
        var counts = await db.PaperSections.AsNoTracking().Where(x => versionIds.Contains(x.PaperVersionId))
            .GroupBy(x => x.PaperVersionId).Select(x => new { VersionId = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.VersionId, x => x.Count);
        var questionCounts = await db.PaperQuestions.AsNoTracking().Where(x => versionIds.Contains(x.PaperVersionId))
            .GroupBy(x => x.PaperVersionId).Select(x => new { VersionId = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.VersionId, x => x.Count);
        var rows = page.Select(x => new PaperRow(x.Paper.Id, x.Paper.Title, x.Paper.DepartmentId, x.DepartmentName,
            x.Paper.Status, x.Paper.CurrentVersionId, x.Version?.VersionNo ?? 0, x.Version?.TotalScore ?? 0,
            x.Version?.DurationMinutes ?? 0, counts.GetValueOrDefault(x.Version?.Id ?? 0),
            questionCounts.GetValueOrDefault(x.Version?.Id ?? 0), x.Paper.UpdatedAt ?? x.Paper.CreatedAt, x.Paper.RowVersion)).ToList();
        return new PagedResult<PaperRow>(rows, total, query.PageIndex, query.PageSize);
    }

    public async Task<PaperDetail?> GetDetailAsync(ExamDbContext db, long id, long? requestedVersionId = null)
    {
        var paper = await db.Papers.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Title, x.DepartmentId, x.Status, x.RowVersion, x.CurrentVersionId,
                DepartmentName = db.Departments.Where(d => d.Id == x.DepartmentId).Select(d => d.Name).First() })
            .FirstOrDefaultAsync();
        if (paper is null) return null;
        var versions = await db.PaperVersions.AsNoTracking().Where(x => x.PaperId == id)
            .OrderByDescending(x => x.VersionNo).ThenByDescending(x => x.Id)
            .Select(x => new PaperVersionRow(x.Id, x.VersionNo, x.Title, x.TotalScore, x.DurationMinutes,
                x.Status, x.PublishedAt, x.Id == paper.CurrentVersionId, x.RowVersion)).ToListAsync();
        if (requestedVersionId.HasValue && versions.All(x => x.Id != requestedVersionId.Value)) return null;
        var draft = versions.FirstOrDefault(x => x.Status == "Draft" && paper.Status != "Archived");
        var selected = requestedVersionId.HasValue ? versions.First(x => x.Id == requestedVersionId.Value)
            : draft ?? versions.FirstOrDefault(x => x.IsCurrent) ?? versions.FirstOrDefault();
        if (selected is null) return null;
        var sections = await db.PaperSections.AsNoTracking().Where(x => x.PaperVersionId == selected.Id)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new PaperSectionRow(x.Id, x.Title, x.SortOrder, x.SelectionMode, x.PickCount,
                x.ScorePerQuestion, x.BankId, x.CategoryId, x.QuestionType, x.Difficulty,
                db.PaperQuestions.Count(q => q.SectionId == x.Id), x.RowVersion)).ToListAsync();
        var questions = await (from pq in db.PaperQuestions.AsNoTracking()
                               join qv in db.QuestionVersions.AsNoTracking() on pq.QuestionVersionId equals qv.Id
                               join q in db.Questions.AsNoTracking() on qv.QuestionId equals q.Id
                               join b in db.QuestionBanks.AsNoTracking() on q.BankId equals b.Id
                               where pq.PaperVersionId == selected.Id
                               orderby pq.SectionId, pq.SortOrder, pq.Id
                               select new PaperQuestionRow(pq.Id, pq.SectionId, pq.QuestionVersionId, q.Id, q.BankId,
                                   b.Name, q.ExternalCode, q.Type, qv.Stem, qv.Difficulty, pq.SortOrder, pq.Score, pq.IsPool)).ToListAsync();
        return new PaperDetail(paper.Id, paper.Title, paper.DepartmentId, paper.DepartmentName, paper.Status,
            paper.RowVersion, paper.CurrentVersionId, selected.Id, selected.VersionNo, selected.Title,
            selected.TotalScore, selected.DurationMinutes, selected.Status, selected.RowVersion, selected.PublishedAt,
            versions, draft?.Id ?? 0, sections, questions, PaperPreviewBuilder.Build(sections, questions));
    }

    public async Task<Paper?> GetPaperTrackedAsync(ExamDbContext db, long id)
        => await db.Papers.FirstOrDefaultAsync(x => x.Id == id);

    public async Task<PaperVersion?> GetVersionTrackedAsync(ExamDbContext db, long id)
        => await db.PaperVersions.FirstOrDefaultAsync(x => x.Id == id);

    public async Task<PaperSection?> GetSectionTrackedAsync(ExamDbContext db, long id)
        => await db.PaperSections.FirstOrDefaultAsync(x => x.Id == id);

    public async Task<PaperQuestion?> GetPaperQuestionTrackedAsync(ExamDbContext db, long id)
        => await db.PaperQuestions.FirstOrDefaultAsync(x => x.Id == id);

    public async Task<long> CreateDraftAsync(ExamDbContext db, CreatePaperInput input, long actorId, DateTime now)
    {
        var paper = new Paper { Title = input.Title.Trim(), DepartmentId = input.DepartmentId, CreatedBy = actorId, CreatedAt = now };
        db.Papers.Add(paper);
        await db.SaveChangesAsync();
        var version = new PaperVersion { PaperId = paper.Id, VersionNo = 1, Title = paper.Title,
            DurationMinutes = input.DurationMinutes, TotalScore = 0, Status = "Draft", CreatedAt = now };
        db.PaperVersions.Add(version);
        await db.SaveChangesAsync();
        paper.CurrentVersionId = version.Id;
        paper.UpdatedAt = now;
        await db.SaveChangesAsync();
        return version.Id;
    }

    public async Task UpdateDraftAsync(ExamDbContext db, Paper paper, PaperVersion version,
        PaperVersionInput input, DateTime now)
    {
        if (version.Status != "Draft" || paper.Status == "Archived") throw new InvalidOperationException("只有未归档草稿可以修改。");
        version.Title = input.Title.Trim();
        version.DurationMinutes = input.DurationMinutes;
        version.RowVersion++;
        paper.Title = input.Title.Trim();
        paper.UpdatedAt = now;
        paper.RowVersion++;
        await ReplaceSectionsAsync(db, version, input.Sections);
    }

    public async Task ReplaceSectionsAsync(ExamDbContext db, PaperVersion version, IReadOnlyList<PaperSectionInput> inputs)
    {
        if (version.Status != "Draft") throw new InvalidOperationException("已发布试卷不可修改。");
        db.PaperQuestions.RemoveRange(db.PaperQuestions.Where(x => x.PaperVersionId == version.Id));
        db.PaperSections.RemoveRange(db.PaperSections.Where(x => x.PaperVersionId == version.Id));
        // 先落删除，避免同一批次中删除旧小节后立即复用其唯一顺序号。
        await db.SaveChangesAsync();
        var sections = inputs.OrderBy(x => x.SortOrder).Select(x => new PaperSection
        {
            PaperVersionId = version.Id, Title = x.Title.Trim(), SortOrder = x.SortOrder,
            SelectionMode = x.SelectionMode, PickCount = x.SelectionMode == "Random" ? x.PickCount : null,
            ScorePerQuestion = x.ScorePerQuestion,
            BankId = x.SelectionMode == "Random" ? x.BankId : null,
            CategoryId = x.SelectionMode == "Random" ? x.CategoryId : null,
            QuestionType = x.SelectionMode == "Random" ? x.QuestionType : null,
            Difficulty = x.SelectionMode == "Random" ? x.Difficulty : null
        }).ToList();
        db.PaperSections.AddRange(sections);
        await db.SaveChangesAsync();
        var sectionIds = sections.ToDictionary(x => x.SortOrder, x => x.Id);
        var questions = new List<PaperQuestion>();
        foreach (var input in inputs.OrderBy(x => x.SortOrder))
        {
            var sort = 0;
            foreach (var questionVersionId in input.QuestionVersionIds.Distinct())
            {
                questions.Add(new PaperQuestion { PaperVersionId = version.Id, SectionId = sectionIds[input.SortOrder],
                    QuestionVersionId = questionVersionId, SortOrder = ++sort, Score = input.ScorePerQuestion,
                    IsPool = input.SelectionMode == "Random" });
            }
        }
        db.PaperQuestions.AddRange(questions);
        await db.SaveChangesAsync();
        await RecalculateTotalAsync(db, version);
        await db.SaveChangesAsync();
    }

    public async Task<PaperVersion?> CopyDraftAsync(ExamDbContext db, long paperId, DateTime now)
    {
        var paper = await GetPaperTrackedAsync(db, paperId);
        if (paper is null || paper.Status == "Archived") return null;
        var sourceId = paper.CurrentVersionId ?? await db.PaperVersions.AsNoTracking().Where(x => x.PaperId == paperId)
            .OrderByDescending(x => x.VersionNo).Select(x => (long?)x.Id).FirstOrDefaultAsync();
        if (!sourceId.HasValue) return null;
        var source = await db.PaperVersions.AsNoTracking().FirstAsync(x => x.Id == sourceId.Value);
        var version = new PaperVersion { PaperId = paperId, VersionNo = await GetNextVersionNoAsync(db, paperId),
            Title = source.Title, TotalScore = source.TotalScore, DurationMinutes = source.DurationMinutes,
            Status = "Draft", CreatedAt = now };
        db.PaperVersions.Add(version);
        await db.SaveChangesAsync();
        var sourceSections = await db.PaperSections.AsNoTracking().Where(x => x.PaperVersionId == source.Id)
            .OrderBy(x => x.SortOrder).ToListAsync();
        db.PaperSections.AddRange(sourceSections.Select(x => new PaperSection { PaperVersionId = version.Id,
            Title = x.Title, SortOrder = x.SortOrder, SelectionMode = x.SelectionMode, PickCount = x.PickCount,
            ScorePerQuestion = x.ScorePerQuestion, BankId = x.BankId, CategoryId = x.CategoryId,
            QuestionType = x.QuestionType, Difficulty = x.Difficulty }));
        await db.SaveChangesAsync();
        var sectionMap = await db.PaperSections.AsNoTracking().Where(x => x.PaperVersionId == version.Id)
            .ToDictionaryAsync(x => x.SortOrder, x => x.Id);
        var sourceQuestions = await db.PaperQuestions.AsNoTracking().Where(x => x.PaperVersionId == source.Id).ToListAsync();
        var sourceSectionOrders = sourceSections.ToDictionary(x => x.Id, x => x.SortOrder);
        db.PaperQuestions.AddRange(sourceQuestions.Select(x => new PaperQuestion { PaperVersionId = version.Id,
            SectionId = sectionMap[sourceSectionOrders[x.SectionId]], QuestionVersionId = x.QuestionVersionId,
            SortOrder = x.SortOrder, Score = x.Score, IsPool = x.IsPool }));
        paper.UpdatedAt = now;
        paper.RowVersion++;
        return version;
    }

    public async Task<bool> PublishAsync(ExamDbContext db, Paper paper, PaperVersion version, DateTime now, long actorId)
    {
        if (paper.Status == "Archived" || version.PaperId != paper.Id || version.Status != "Draft") return false;
        version.Status = "Published";
        version.PublishedAt = now;
        version.PublishedBy = actorId;
        version.RowVersion++;
        // 先发布版本，再切换试卷指针；数据库 BEFORE 触发器要求指针已指向 Published 版本。
        await db.SaveChangesAsync();
        paper.Status = "Published";
        paper.CurrentVersionId = version.Id;
        paper.UpdatedAt = now;
        paper.RowVersion++;
        return true;
    }

    public async Task<bool> ArchiveAsync(ExamDbContext db, Paper paper, DateTime now)
    {
        if (paper.Status == "Archived") return false;
        // 同一事务冻结全部版本：当前发布版本、历史发布版本和未发布修订草稿都不可再改子行。
        await db.PaperVersions.Where(x => x.PaperId == paper.Id && x.Status != "Archived")
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "Archived").SetProperty(x => x.RowVersion, x => x.RowVersion + 1));
        paper.Status = "Archived";
        paper.ArchivedAt = now;
        paper.UpdatedAt = now;
        paper.RowVersion++;
        return true;
    }

    public async Task ApplyBlueprintAsync(ExamDbContext db, Paper paper, PaperVersion version, PaperSection section,
        IReadOnlyList<long> questionVersionIds, DateTime now)
    {
        if (version.Status != "Draft" || section.PaperVersionId != version.Id || section.SelectionMode != "Random")
            throw new InvalidOperationException("只能对草稿随机小节应用蓝图。");
        db.PaperQuestions.RemoveRange(db.PaperQuestions.Where(x => x.SectionId == section.Id));
        await db.SaveChangesAsync();
        var sort = 0;
        db.PaperQuestions.AddRange(questionVersionIds.Distinct().Select(id => new PaperQuestion
        {
            PaperVersionId = version.Id, SectionId = section.Id, QuestionVersionId = id,
            SortOrder = ++sort, Score = section.ScorePerQuestion, IsPool = true
        }));
        section.RowVersion++;
        version.RowVersion++;
        paper.UpdatedAt = now;
        paper.RowVersion++;
        await db.SaveChangesAsync();
    }

    public async Task AddQuestionAsync(ExamDbContext db, Paper paper, PaperVersion version, PaperSection section,
        long questionVersionId, DateTime now)
    {
        if (version.Status != "Draft" || section.PaperVersionId != version.Id) throw new InvalidOperationException("只能向草稿试卷添加题目。");
        var next = await db.PaperQuestions.AsNoTracking().Where(x => x.SectionId == section.Id)
            .Select(x => (int?)x.SortOrder).MaxAsync() + 1 ?? 1;
        db.PaperQuestions.Add(new PaperQuestion { PaperVersionId = version.Id, SectionId = section.Id,
            QuestionVersionId = questionVersionId, SortOrder = next, Score = section.ScorePerQuestion,
            IsPool = section.SelectionMode == "Random" });
        version.RowVersion++;
        paper.UpdatedAt = now;
        paper.RowVersion++;
        await RecalculateTotalAsync(db, version);
    }

    public async Task DeleteQuestionAsync(ExamDbContext db, Paper paper, PaperVersion version, PaperQuestion question, DateTime now)
    {
        if (version.Status != "Draft" || question.PaperVersionId != version.Id) throw new InvalidOperationException("只能删除草稿试卷题目。");
        db.PaperQuestions.Remove(question);
        version.RowVersion++;
        paper.UpdatedAt = now;
        paper.RowVersion++;
        await RecalculateTotalAsync(db, version);
    }

    public async Task RecalculateTotalAsync(ExamDbContext db, PaperVersion version)
    {
        var sections = await db.PaperSections.AsNoTracking().Where(x => x.PaperVersionId == version.Id).ToListAsync();
        var fixedSums = await db.PaperQuestions.AsNoTracking().Where(x => x.PaperVersionId == version.Id && !x.IsPool)
            .GroupBy(x => x.SectionId).Select(x => new { SectionId = x.Key, Sum = x.Sum(q => q.Score) })
            .ToDictionaryAsync(x => x.SectionId, x => x.Sum);
        version.TotalScore = sections.Sum(x => x.SelectionMode == "Random"
            ? (x.PickCount ?? 0) * x.ScorePerQuestion
            : fixedSums.GetValueOrDefault(x.Id, 0));
    }

    public async Task<int> GetNextVersionNoAsync(ExamDbContext db, long paperId)
        => await db.PaperVersions.AsNoTracking().Where(x => x.PaperId == paperId).Select(x => (int?)x.VersionNo).MaxAsync() + 1 ?? 1;

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
