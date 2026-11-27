using System.Text.Json;
using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class ImportRepository(IUnitOfWork uow, QuestionRepository questions)
{
    private ExamDbContext Db => uow.Db;

    public async Task<ImportBatch?> GetByHashAsync(long bankId, string hash)
        => await Db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(x => x.BankId == bankId && x.FileHash == hash);

    public async Task<QuestionImportPreview?> GetPreviewAsync(long id, bool replayed = false)
    {
        var batch = await Db.ImportBatches.AsNoTracking().Where(x => x.Id == id).FirstOrDefaultAsync();
        return batch is null ? null : ToPreview(batch, replayed);
    }

    public async Task<PagedResult<QuestionImportPreview>> GetPagedAsync(QuestionImportQuery query, IReadOnlyList<long>? allowedDepartmentIds)
    {
        var db = Db;
        var batches = db.ImportBatches.AsNoTracking();
        if (allowedDepartmentIds is not null)
        {
            var allowed = allowedDepartmentIds.ToArray();
            batches = batches.Where(x => db.QuestionBanks.Any(b => b.Id == x.BankId
                && (allowed.Contains(b.DepartmentId) || (b.IsShared && db.Departments.Any(target => allowed.Contains(target.Id)
                    && db.Departments.Any(owner => owner.Id == b.DepartmentId && target.Path.StartsWith(owner.Path)))))));
        }
        if (query.BankId.HasValue) batches = batches.Where(x => x.BankId == query.BankId.Value);
        if (!string.IsNullOrWhiteSpace(query.Status)) batches = batches.Where(x => x.Status == query.Status);
        var total = await batches.CountAsync();
        var rows = await batches.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((query.PageIndex - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return new PagedResult<QuestionImportPreview>(rows.Select(x => ToPreview(x, false)).ToList(), total, query.PageIndex, query.PageSize);
    }

    public async Task<QuestionImportBatchDetail?> GetDetailAsync(long id, int pageIndex, int pageSize)
    {
        var batch = await Db.ImportBatches.AsNoTracking().Where(x => x.Id == id).FirstOrDefaultAsync();
        if (batch is null) return null;
        var query = Db.ImportRows.AsNoTracking().Where(x => x.BatchId == id);
        var total = await query.CountAsync();
        var rows = await query.OrderBy(x => x.RowNo).Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync();
        var terminal = batch.Status is "Completed" or "PartiallyFailed" or "Failed";
        return new QuestionImportBatchDetail(ToPreview(batch, false), batch.BankId, batch.FileName, batch.Mode,
            batch.CreatedAt, batch.ConfirmedAt, batch.FinishedAt, rows.Select(x => ToRow(x, terminal)).ToList(), total, pageIndex, pageSize);
    }

    public async Task<ImportBatch?> GetTrackedAsync(ExamDbContext db, long id)
    {
        return await db.ImportBatches.FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task AddPreviewAsync(ExamDbContext db, ImportBatch batch, IReadOnlyList<ImportRow> rows)
    {
        // 显式事务内先落批次，行才能引用真实的 batch_id。
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync();
        db.ImportRows.AddRange(rows.Select(x => new ImportRow
        {
            BatchId = batch.Id, RowNo = x.RowNo, ExternalCode = x.ExternalCode, Status = x.Status,
            RawJson = x.RawJson, AnswerSummary = x.AnswerSummary, ErrorJson = x.ErrorJson,
            WarningJson = x.WarningJson, CreatedAt = x.CreatedAt
        }));
    }

    public async Task<bool> MarkImportingAsync(ExamDbContext db, long id, string mode, long expectedRowVersion, DateTime now)
    {
        var batch = await GetTrackedAsync(db, id);
        if (batch is null || batch.Status != "Previewed" || batch.Mode != mode || batch.RowVersion != expectedRowVersion)
            return false;
        batch.Status = "Importing";
        batch.ConfirmedAt = now;
        batch.RowVersion++;
        return true;
    }

    public async Task ExcludeRowsAsync(ExamDbContext db, long batchId, IReadOnlyList<int> rowNos, DateTime now)
    {
        var numbers = rowNos.Distinct().ToArray();
        var rows = await db.ImportRows.Where(x => x.BatchId == batchId && x.Status == "Warning" && numbers.Contains(x.RowNo)).ToListAsync();
        foreach (var row in rows)
        {
            row.Status = "Excluded";
            row.UpdatedAt = now;
            row.RowVersion++;
        }
    }

    public async Task<Question?> CreateImportedQuestionAsync(ExamDbContext db, ImportBatch batch, ImportRow row, ImportQuestionData data, DateTime now)
    {
        if (!string.IsNullOrEmpty(data.ExternalCode)
            && (db.Questions.Local.Any(x => x.BankId == batch.BankId && x.ExternalCode == data.ExternalCode)
                || await db.Questions.AsNoTracking().AnyAsync(x => x.BankId == batch.BankId && x.ExternalCode == data.ExternalCode)))
        {
            row.Status = "Excluded";
            row.ErrorJson = JsonSerializer.Serialize(new[] { "题库内外部编码已存在，未覆盖原题" });
            row.UpdatedAt = now;
            row.RowVersion++;
            return null;
        }
        var question = new Question
        {
            BankId = batch.BankId, ExternalCode = data.ExternalCode, Type = data.Type, Status = "Draft", CreatedAt = now
        };
        var version = new QuestionVersion
        {
            VersionNo = 1, Stem = data.Stem, AnswerJson = data.AnswerJson, Difficulty = data.Difficulty,
            DefaultScore = data.DefaultScore, Analysis = data.Analysis, Status = "Draft", Source = "Import",
            ImportBatchId = batch.Id, ImportRowId = row.Id, CreatedAt = now
        };
        var options = data.Options.Select(x => new QuestionOption
        {
            Label = x.Label, Content = x.Content, IsCorrect = x.IsCorrect, SortOrder = x.SortOrder
        }).ToArray();
        var tags = await questions.EnsureTagsAsync(db, data.TagNames, now);
        var categories = data.CategoryIds.Distinct().Select(x => new QuestionCategoryLink { CategoryId = x }).ToArray();
        await questions.AddQuestionAsync(db, question, version, options, tags, categories);
        return question;
    }

    public async Task CompleteBatchAsync(ExamDbContext db, ImportBatch batch, IReadOnlyList<ImportRowOutcome> outcomes, DateTime now)
    {
        if (batch.Status != "Importing") throw new InvalidOperationException("只有导入中的批次可以完成。");
        var rows = await db.ImportRows.Where(x => x.BatchId == batch.Id).ToListAsync();
        var byId = rows.ToDictionary(x => x.Id);
        foreach (var outcome in outcomes)
        {
            if (!byId.TryGetValue(outcome.RowId, out var row) || row.Status is "Imported" or "Excluded") continue;
            row.Status = outcome.Error is null && outcome.QuestionId.HasValue ? "Imported" : "Excluded";
            row.QuestionId = row.Status == "Imported" ? outcome.QuestionId : null;
            if (outcome.Error is not null) row.ErrorJson = JsonSerializer.Serialize(new[] { outcome.Error });
            row.UpdatedAt = now;
            row.RowVersion++;
        }
        foreach (var row in rows.Where(x => x.Status is "Valid" or "Warning" or "Invalid"))
        {
            row.Status = "Excluded";
            row.UpdatedAt = now;
            row.RowVersion++;
        }
        // 终态不保留完整题干/选项，错误报告只使用编码、状态和校验原因。
        foreach (var row in rows) row.RawJson = "{}";
        var failedRowIds = outcomes.Where(x => !string.IsNullOrWhiteSpace(x.Error)).Select(x => x.RowId).ToHashSet();
        batch.ImportedRows = rows.Count(x => x.Status == "Imported");
        var importedQuestionIds = rows.Where(x => x.Status == "Imported" && x.QuestionId.HasValue)
            .Select(x => x.QuestionId!.Value).Distinct().ToArray();
        var draftVersions = await db.QuestionVersions.AsNoTracking()
            .Where(x => x.Status == "Draft" && importedQuestionIds.Contains(x.QuestionId))
            .Select(x => new { x.QuestionId, x.Id }).ToListAsync();
        var draftByQuestion = draftVersions.GroupBy(x => x.QuestionId).ToDictionary(x => x.Key, x => x.Max(v => v.Id));
        var importedQuestions = await db.Questions.Where(x => importedQuestionIds.Contains(x.Id)).ToListAsync();
        foreach (var question in importedQuestions)
        {
            if (draftByQuestion.TryGetValue(question.Id, out var draftVersionId))
            {
                question.CurrentVersionId = draftVersionId;
                question.UpdatedAt = now;
            }
        }
        batch.FailedRows = rows.Count(x => x.Status == "Invalid" || failedRowIds.Contains(x.Id));
        batch.ExcludedRows = rows.Count(x => x.Status == "Excluded" && !failedRowIds.Contains(x.Id));
        batch.Status = batch.FailedRows == 0 ? "Completed" : batch.ImportedRows > 0 ? "PartiallyFailed" : "Failed";
        batch.FinishedAt = now;
        batch.RowVersion++;
    }

    private static QuestionImportPreview ToPreview(ImportBatch batch, bool replayed)
        => new(batch.Id, batch.Status, batch.TotalRows, batch.ValidRows, batch.InvalidRows, batch.WarningRows,
            batch.DuplicateRows, batch.ImportedRows, batch.FailedRows, batch.ExcludedRows, batch.RowVersion, replayed);

    private static QuestionImportRowRow ToRow(ImportRow row, bool terminal)
    {
        using var raw = JsonDocument.Parse(row.RawJson);
        return new QuestionImportRowRow(row.RowNo, row.ExternalCode, row.Status,
            !terminal && raw.RootElement.TryGetProperty("stem", out var stem) ? stem.ToString() : null,
            row.AnswerSummary, string.Join("\n", ReadStrings(row.ErrorJson)), string.Join("\n", ReadStrings(row.WarningJson)));
    }

    private static IReadOnlyList<string> ReadStrings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(x => x.ToString()).ToArray();
    }
}
