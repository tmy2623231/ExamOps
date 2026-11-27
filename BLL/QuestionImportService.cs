using System.Text.Json;
using ExamOps.BLL.Validators;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Infra.Import;
using ExamOps.Infra.Storage;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExamOps.BLL;

public sealed class QuestionImportService(
    IUnitOfWork uow,
    QuestionRepository questions,
    ImportRepository imports,
    QuestionService questionService,
    DataScopeService scope,
    AuditRepository audit,
    ICurrentUser currentUser,
    IClock clock,
    IFileStorage storage,
    ILogger<QuestionImportService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<QuestionImportPreview>> PreviewAsync(long bankId, Stream content, string fileName, string mode,
        CancellationToken cancellationToken = default)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.QuestionImport))
            return Result<QuestionImportPreview>.Fail("权限不足", "PERMISSION_DENIED");
        mode = mode.Trim();
        if (mode is not ("AllOrNothing" or "SkipInvalid"))
            return Result<QuestionImportPreview>.Fail("导入模式不正确", "INVALID_MODE");
        var bank = await questionService.GetBankAsync(bankId);
        if (bank is null) return Result<QuestionImportPreview>.Fail("题库不存在", "NOT_FOUND");
        if (bank.Status != "Active" || !(await scope.EnsureDepartmentAccessAsync(bank.DepartmentId, Permissions.QuestionImport)).IsSuccess)
            return Result<QuestionImportPreview>.Fail("无权在该题库导入题目或题库已归档", "OUT_OF_SCOPE");

        StoredImportFile? stored = null;
        var committed = false;
        try
        {
            stored = await storage.SaveImportAsync(content, fileName, cancellationToken);
            await using var stream = await storage.OpenImportAsync(stored.RelativePath, cancellationToken);
            var validation = ImportFileValidator.Validate(stream, fileName);
            if (!validation.IsSuccess) return Result<QuestionImportPreview>.Fail(validation.Error!, validation.Code);
            stream.Position = 0;
            var parsed = XlsxQuestionParser.Parse(stream);
            if (!parsed.IsSuccess) return Result<QuestionImportPreview>.Fail(parsed.Error!, parsed.ErrorCode);

            var existing = await imports.GetByHashAsync(bankId, stored.Sha256);
            if (existing is not null)
            {
                var replay = await imports.GetPreviewAsync(existing.Id, true)
                    ?? throw new InvalidOperationException("重复导入批次读取失败。");
                return Result<QuestionImportPreview>.Ok(replay);
            }

            var existingCodes = await uow.Db.Questions.AsNoTracking().Where(x => x.BankId == bankId && x.ExternalCode != null)
                .Select(x => x.ExternalCode!).ToListAsync();
            var codeSet = existingCodes.ToHashSet(StringComparer.Ordinal);
            var now = clock.UtcNow;
            var rows = new List<ImportRow>();
            var duplicateRows = 0;
            foreach (var parsedRow in parsed.Rows)
            {
                var errors = parsedRow.Errors.ToList();
                var duplicate = parsedRow.ExternalCode is not null && codeSet.Contains(parsedRow.ExternalCode);
                if (duplicate)
                {
                    errors.Add("题库内外部编码已存在，未覆盖原题。");
                    duplicateRows++;
                }
                var data = ToImportData(bankId, parsedRow);
                if (data is null && errors.Count == 0) errors.Add("题目内容未通过导入校验。");
                var status = errors.Count > 0 ? "Invalid" : parsedRow.Warnings.Count > 0 ? "Warning" : "Valid";
                var rawData = data is null ? (object)new
                {
                    externalCode = parsedRow.ExternalCode, type = parsedRow.Type, stem = parsedRow.Stem,
                    difficulty = parsedRow.Difficulty, defaultScore = parsedRow.DefaultScore, analysis = parsedRow.Analysis,
                    answerSummary = AnswerSummary(parsedRow)
                } : data;
                rows.Add(new ImportRow
                {
                    RowNo = parsedRow.RowNo, ExternalCode = parsedRow.ExternalCode, Status = status,
                    RawJson = JsonSerializer.Serialize(rawData, JsonOptions),
                    AnswerSummary = AnswerSummary(parsedRow),
                    ErrorJson = JsonSerializer.Serialize(errors, JsonOptions),
                    WarningJson = JsonSerializer.Serialize(parsedRow.Warnings, JsonOptions), CreatedAt = now
                });
            }
            var batch = new ImportBatch
            {
                BankId = bankId, FileName = Path.GetFileName(fileName), FileKey = stored.RelativePath, FileHash = stored.Sha256,
                Mode = mode, Status = "Previewed", TotalRows = rows.Count,
                ValidRows = rows.Count(x => x.Status != "Invalid"), InvalidRows = rows.Count(x => x.Status == "Invalid"),
                WarningRows = rows.Count(x => x.Status == "Warning"), DuplicateRows = duplicateRows,
                WarningJson = JsonSerializer.Serialize(parsed.Rows.SelectMany(x => x.Warnings).Distinct().ToArray(), JsonOptions),
                CreatedBy = ActorId(), CreatedAt = now
            };
            try
            {
                await uow.ExecuteInTransactionAsync(async db =>
                {
                    await imports.AddPreviewAsync(db, batch, rows);
                    await db.SaveChangesAsync();
                    await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.import.preview", "ImportBatch", batch.Id,
                        after: new { batch.BankId, batch.FileHash, batch.TotalRows, batch.ValidRows, batch.InvalidRows, batch.Mode });
                    await db.SaveChangesAsync();
                });
                committed = true;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex, "uq_import_batch_hash"))
            {
                var replay = await imports.GetByHashAsync(bankId, stored.Sha256)
                    ?? throw new InvalidOperationException("重复导入批次读取失败。");
                var preview = await imports.GetPreviewAsync(replay.Id, true)
                    ?? throw new InvalidOperationException("重复导入批次读取失败。");
                return Result<QuestionImportPreview>.Ok(preview);
            }
            var result = await imports.GetPreviewAsync(batch.Id)
                ?? throw new InvalidOperationException("导入预览读取失败。");
            return Result<QuestionImportPreview>.Ok(result);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or DbUpdateConcurrencyException)
        {
            logger.LogWarning(ex, "导入预览未完成。BankId={BankId}", bankId);
            return Result<QuestionImportPreview>.Fail("导入预览失败，请检查文件后重试。", "IMPORT_PREVIEW_FAILED");
        }
        finally
        {
            if (stored is not null && !committed) storage.DeleteImport(stored.RelativePath);
        }
    }

    public async Task<Result<QuestionImportPreview>> ConfirmAsync(long bankId, ImportConfirmInput input,
        CancellationToken cancellationToken = default)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(Permissions.QuestionImport))
            return Result<QuestionImportPreview>.Fail("权限不足", "PERMISSION_DENIED");
        if (input.Mode is not ("AllOrNothing" or "SkipInvalid"))
            return Result<QuestionImportPreview>.Fail("导入模式不正确", "INVALID_MODE");
        var bank = await questionService.GetBankAsync(bankId);
        if (bank is null) return Result<QuestionImportPreview>.Fail("题库不存在", "NOT_FOUND");
        if (bank.Status != "Active" || !(await scope.EnsureDepartmentAccessAsync(bank.DepartmentId, Permissions.QuestionImport)).IsSuccess)
            return Result<QuestionImportPreview>.Fail("无权在该题库导入题目或题库已归档", "OUT_OF_SCOPE");
        var detail = await imports.GetDetailAsync(input.BatchId, 1, 5000);
        if (detail is null || detail.BankId != bankId) return Result<QuestionImportPreview>.Fail("导入批次不存在", "NOT_FOUND");
        if (detail.Preview.Status is "Completed" or "PartiallyFailed" or "Failed")
            return Result<QuestionImportPreview>.Ok(detail.Preview);
        if (detail.Preview.Status != "Previewed") return Result<QuestionImportPreview>.Fail("当前批次不能确认导入", "INVALID_STATE");
        if (detail.Preview.RowVersion != input.RowVersion) return ConcurrencyFailure<QuestionImportPreview>();
        if (input.Mode != detail.Mode) return Result<QuestionImportPreview>.Fail("导入模式与预览不一致", "MODE_MISMATCH");
        if (input.Mode == "AllOrNothing" && detail.Preview.InvalidRows > 0)
            return Result<QuestionImportPreview>.Fail("存在错误行，全部成功模式不会写入任何题目。", "INVALID_ROWS");
        var excluded = input.ExcludeWarningRowNos?.Distinct().ToArray() ?? [];
        if (excluded.Any(rowNo => !detail.Rows.Any(x => x.RowNo == rowNo && x.Status == "Warning")))
            return Result<QuestionImportPreview>.Fail("只能排除预览中的警告行。", "INVALID_EXCLUSION");

        Result<QuestionImportPreview>? outcome = null;
        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                if (!await imports.MarkImportingAsync(db, input.BatchId, input.Mode, input.RowVersion, now))
                {
                    outcome = ConcurrencyFailure<QuestionImportPreview>();
                    return;
                }
                await imports.ExcludeRowsAsync(db, input.BatchId, excluded, now);
                var batch = await imports.GetTrackedAsync(db, input.BatchId)
                    ?? throw new InvalidOperationException("导入批次丢失。");
                var rows = await db.ImportRows.Where(x => x.BatchId == batch.Id && (x.Status == "Valid" || x.Status == "Warning"))
                    .OrderBy(x => x.RowNo).ToListAsync();
                var existingCodes = await db.Questions.AsNoTracking().Where(x => x.BankId == bankId && x.ExternalCode != null)
                    .Select(x => x.ExternalCode!).ToListAsync();
                var codes = existingCodes.ToHashSet(StringComparer.Ordinal);
                var outcomes = new List<ImportRowOutcome>();
                foreach (var row in rows)
                {
                    var data = JsonSerializer.Deserialize<ImportQuestionData>(row.RawJson, JsonOptions)
                        ?? throw new InvalidDataException("导入行数据损坏。");
                    var duplicate = data.ExternalCode is not null && codes.Contains(data.ExternalCode);
                    if (duplicate)
                    {
                        if (batch.Mode == "AllOrNothing") throw new ImportConflictException();
                        outcomes.Add(new ImportRowOutcome(row.Id, null, "题库内外部编码已存在，未覆盖原题。"));
                        continue;
                    }
                    var question = await imports.CreateImportedQuestionAsync(db, batch, row, data, now);
                    if (question is null)
                    {
                        if (batch.Mode == "AllOrNothing") throw new ImportConflictException();
                        outcomes.Add(new ImportRowOutcome(row.Id, null, "题库内外部编码已存在，未覆盖原题。"));
                        continue;
                    }
                    outcomes.Add(new ImportRowOutcome(row.Id, question.Id, null));
                    if (question.ExternalCode is not null) codes.Add(question.ExternalCode);
                }
                await db.SaveChangesAsync();
                await imports.CompleteBatchAsync(db, batch, outcomes, now);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "question.import.confirm", "ImportBatch", batch.Id,
                    after: new { batch.BankId, batch.Mode, batch.ImportedRows, batch.FailedRows, batch.ExcludedRows, batch.Status });
                await db.SaveChangesAsync();
                outcome = Result<QuestionImportPreview>.Ok(new(batch.Id, batch.Status, batch.TotalRows, batch.ValidRows,
                    batch.InvalidRows, batch.WarningRows, batch.DuplicateRows, batch.ImportedRows, batch.FailedRows,
                    batch.ExcludedRows, batch.RowVersion, false));
            });
            return outcome!;
        }
        catch (ImportConflictException)
        {
            return Result<QuestionImportPreview>.Fail("外部编码已被其他操作占用，本次未写入任何题目。", "DUPLICATE_EXTERNAL_CODE");
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure<QuestionImportPreview>();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            logger.LogWarning(ex, "导入确认发生唯一约束冲突。BatchId={BatchId}", input.BatchId);
            return Result<QuestionImportPreview>.Fail("导入数据发生冲突，本次事务已全部回滚。", "IMPORT_CONFLICT");
        }
    }

    public async Task<Result<QuestionImportBatchDetail>> GetDetailAsync(long batchId, int pageIndex, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var access = await EnsureReadAccessAsync(batchId);
        if (!access.IsSuccess) return Result<QuestionImportBatchDetail>.Fail(access.Error!, access.Code);
        pageIndex = Math.Max(1, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var detail = await imports.GetDetailAsync(batchId, pageIndex, pageSize);
        return detail is null ? Result<QuestionImportBatchDetail>.Fail("导入批次不存在", "NOT_FOUND") : Result<QuestionImportBatchDetail>.Ok(detail);
    }

    public async Task<Result<IReadOnlyList<QuestionImportRowRow>>> GetErrorsAsync(long batchId, CancellationToken cancellationToken = default)
    {
        var access = await EnsureReadAccessAsync(batchId);
        if (!access.IsSuccess) return Result<IReadOnlyList<QuestionImportRowRow>>.Fail(access.Error!, access.Code);
        var detail = await imports.GetDetailAsync(batchId, 1, 5001);
        if (detail is null) return Result<IReadOnlyList<QuestionImportRowRow>>.Fail("导入批次不存在", "NOT_FOUND");
        return Result<IReadOnlyList<QuestionImportRowRow>>.Ok(detail.Rows
            .Where(x => !string.IsNullOrWhiteSpace(x.Errors) || !string.IsNullOrWhiteSpace(x.Warnings)).ToArray());
    }

    public async Task<Result<byte[]>> GetErrorReportAsync(long batchId, CancellationToken cancellationToken = default)
    {
        var access = await EnsureReadAccessAsync(batchId);
        if (!access.IsSuccess) return Result<byte[]>.Fail(access.Error!, access.Code);
        var detail = await imports.GetDetailAsync(batchId, 1, 5001);
        return detail is null
            ? Result<byte[]>.Fail("导入批次不存在", "NOT_FOUND")
            : Result<byte[]>.Ok(ImportErrorReport.CreateCsv(detail));
    }

    private async Task<Result> EnsureReadAccessAsync(long batchId)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || (!currentUser.HasPermission(Permissions.QuestionImport) && !currentUser.HasPermission(Permissions.QuestionRead)))
            return Result.Fail("权限不足", "PERMISSION_DENIED");
        var detail = await imports.GetDetailAsync(batchId, 1, 1);
        if (detail is null) return Result.Fail("导入批次不存在", "NOT_FOUND");
        if (currentUser.HasPermission(Permissions.QuestionImport))
        {
            var importBank = await questions.GetBankAsync(detail.BankId);
            return importBank is not null && (await scope.EnsureDepartmentAccessAsync(importBank.DepartmentId, Permissions.QuestionImport)).IsSuccess
                ? Result.Ok() : Result.Fail("无权查看该导入批次", "OUT_OF_SCOPE");
        }
        var bank = await questionService.GetBankAsync(detail.BankId);
        return bank is not null ? Result.Ok() : Result.Fail("无权查看该导入批次", "OUT_OF_SCOPE");
    }

    private static ImportQuestionData? ToImportData(long bankId, QuestionImportParsedRow row)
    {
        var correct = row.Type == "TrueFalse"
            ? [row.TrueFalseAnswer ? "A" : "B"]
            : row.Type == "SingleChoice" ? [row.AnswerValue ?? ""] : row.AnswerValues;
        var options = row.Type == "TrueFalse"
            ? [new QuestionOptionInput("A", "正确", row.TrueFalseAnswer, 1), new QuestionOptionInput("B", "错误", !row.TrueFalseAnswer, 2)]
            : row.Options.Select((x, index) => new QuestionOptionInput(x.Label, x.Text, correct.Contains(x.Label, StringComparer.Ordinal), index + 1)).ToArray();
        var validated = QuestionValidator.Validate(new QuestionEditInput(bankId, row.ExternalCode, row.Type, row.Stem,
            row.Difficulty, row.DefaultScore, row.Analysis, options, row.TagNames, [], row.AnswerValue ?? "", row.AnswerValues, row.TrueFalseAnswer));
        if (!validated.IsSuccess) return null;
        var value = validated.Value!;
        return new ImportQuestionData(value.ExternalCode, value.Type, value.Stem, value.Difficulty, value.DefaultScore,
            value.Analysis, value.AnswerJson, value.Options, value.TagNames, []);
    }

    private static string AnswerSummary(QuestionImportParsedRow row) => row.Type == "TrueFalse"
        ? (row.TrueFalseAnswer ? "正确" : "错误")
        : row.Type == "SingleChoice" ? row.AnswerValue ?? "" : string.Join(',', row.AnswerValues);

    private long ActorId() => currentUser.Id ?? throw new InvalidOperationException("认证用户缺少 ID");
    private static Result<T> ConcurrencyFailure<T>() => Result<T>.Fail("导入批次已被其他操作处理，请刷新。", "CONCURRENCY_CONFLICT");
    private static bool IsUniqueViolation(DbUpdateException exception, string? constraint = null)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            && (constraint is null || postgres.ConstraintName == constraint);

    private sealed class ImportConflictException : Exception;
}
