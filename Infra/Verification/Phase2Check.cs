using System.Security.Claims;
using System.Text.RegularExpressions;
using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Infra.Storage;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ExamOps.Infra.Verification;

public sealed class Phase2Check(
    IServiceScopeFactory scopeFactory,
    IHttpContextAccessor httpContextAccessor,
    IClock clock,
    IFileStorage storage,
    IWebHostEnvironment environment)
{
    public async Task RunAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var uow = services.GetRequiredService<IUnitOfWork>();
        var admin = await uow.Db.Users.AsNoTracking().SingleAsync(x => x.Username == "admin");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("examops_user_id", admin.Id.ToString()),
                new Claim("examops_row_version", admin.RowVersion.ToString()),
                new Claim(ClaimTypes.Name, "admin")
            ], "ExamOps.Check"))
        };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        httpContextAccessor.HttpContext = context;
        string? importedFile = null;
        try
        {
            await uow.ExecuteInTransactionAsync(async _ =>
            {
                var db = uow.Db;
                // 外层事务回滚后恢复管理员原状态，自查不改变本地登录账号。
                var adminAccount = await db.Users.SingleAsync(x => x.Id == admin.Id);
            if (adminAccount.Status != "Active")
            {
                adminAccount.Status = "Active";
                adminAccount.RowVersion++;
                adminAccount.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync();
            }
            var currentUser = services.GetRequiredService<ICurrentUser>();
            await currentUser.LoadAsync();
            Check(currentUser.HasPermission(Permissions.QuestionCreate), "admin can manage questions");
            var root = await db.Departments.AsNoTracking().SingleAsync(x => x.Path == "001.");
            var suffix = clock.UtcNow.Ticks.ToString();
            var questions = services.GetRequiredService<QuestionService>();
            var imports = services.GetRequiredService<QuestionImportService>();
            var organizations = services.GetRequiredService<OrganizationService>();
            var departments = services.GetRequiredService<DepartmentRepository>();
            var audit = services.GetRequiredService<AuditRepository>();

            var bankResult = await questions.CreateBankAsync($"自查题库-{suffix}", root.Id, true);
            Check(bankResult.IsSuccess, "create question bank");
            var bankId = bankResult.Value!;
            var bank = await questions.GetBankAsync(bankId) ?? throw new InvalidOperationException("题库读取失败。");
            var categoryResult = await questions.CreateCategoryAsync(bankId, "基础知识", null);
            Check(categoryResult.IsSuccess, "create category");
            var childResult = await questions.CreateCategoryAsync(bankId, "计算机基础", categoryResult.Value!);
            Check(childResult.IsSuccess, "create child category");
            var tagResult = await questions.CreateTagAsync($"自查标签-{suffix}");
            Check(tagResult.IsSuccess, "create tag");

            var childDepartmentResult = await organizations.CreateDepartmentAsync($"自查子部门-{suffix}", root.Id);
            Check(childDepartmentResult.IsSuccess, "create child department for shared-bank scope");
            var childDepartmentId = (await db.Departments.AsNoTracking()
                .SingleAsync(x => x.Name == $"自查子部门-{suffix}")).Id;
            var readerRoles = new[]
            {
                new RoleAssignmentData(RoleCodes.QuestionManager, childDepartmentId, "DepartmentSubtree",
                    [Permissions.QuestionRead, Permissions.QuestionCreate, Permissions.QuestionUpdate, Permissions.QuestionPublish])
            };
            var reader = new ScopeCheckUser(readerRoles, admin.Id, "shared-reader");
            var readerQuestions = new QuestionService(uow, services.GetRequiredService<QuestionRepository>(), departments,
                new DataScopeService(reader, departments, uow), audit, reader, clock);
            Check(await readerQuestions.GetBankAsync(bankId) is not null
                && (await readerQuestions.GetBanksAsync()).Any(x => x.Id == bankId),
                "shared bank is readable from an authorized child department");
            var privateBankResult = await questions.CreateBankAsync($"自查私有题库-{suffix}", root.Id, false);
            Check(privateBankResult.IsSuccess && await readerQuestions.GetBankAsync(privateBankResult.Value!) is null
                && (await readerQuestions.GetBanksAsync()).All(x => x.Id != privateBankResult.Value),
                "non-shared parent bank stays invisible to child department");
            var outOfScopeCreate = await readerQuestions.CreateAsync(new QuestionEditInput(bankId, $"CHECK-SCOPE-{suffix}",
                "SingleChoice", "范围外写入", "Easy", 1m, null,
                [new("A", "甲", true, 1), new("B", "乙", false, 2)], [], [], "A", [], false));
            Check(!outOfScopeCreate.IsSuccess && outOfScopeCreate.Code == "OUT_OF_SCOPE",
                "shared-bank read access does not grant parent-department write access");
            var deniedUser = new ScopeCheckUser([], admin.Id, "denied");
            var deniedQuestions = new QuestionService(uow, services.GetRequiredService<QuestionRepository>(), departments,
                new DataScopeService(deniedUser, departments, uow), audit, deniedUser, clock);
            var denied = false;
            try { await deniedQuestions.GetBanksAsync(); }
            catch (UnauthorizedAccessException) { denied = true; }
            Check(denied, "question read permission is enforced");

            var input = new QuestionEditInput(bankId, $"CHECK-{suffix}", "SingleChoice", "下列哪项是操作系统？",
                "Easy", 5m, "用于验证发布与副本。",
                [new("A", "Windows", true, 1), new("B", "Word", false, 2), new("C", "Excel", false, 3)],
                [tagResult.Value!.Name], [categoryResult.Value!, childResult.Value!], "A", [], false);
            var created = await questions.CreateAsync(input);
            Check(created.IsSuccess, "create single-choice question");
            var detail = await questions.GetDetailAsync(created.Value!.QuestionId) ?? throw new InvalidOperationException("题目读取失败。");
            Check(detail.Options.Count == 3 && detail.AnswerValue == "A" && detail.CategoryIds.Count == 2, "question answer and category links");
            var publishProbe = await db.QuestionVersions.AsNoTracking().SingleAsync(x => x.Id == created.Value!.DraftVersionId);
            var publishOptions = await db.QuestionOptions.AsNoTracking().Where(x => x.QuestionVersionId == publishProbe.Id)
                .Select(x => new { x.Label, x.IsCorrect }).OrderBy(x => x.Label).ToListAsync();
            Check(publishProbe.AnswerJson == "{\"value\": \"A\"}" && publishOptions.Count(x => x.IsCorrect) == 1
                && publishOptions.Single(x => x.Label == "A").IsCorrect, "publish fixture matches database answer guard");
            await db.Database.ExecuteSqlRawAsync("SELECT fn_assert_question_answer({0}, {1}::jsonb, 'SingleChoice', true)",
                publishProbe.Id, publishProbe.AnswerJson);
            var duplicate = await questions.CreateAsync(input);
            Check(!duplicate.IsSuccess && duplicate.Code == "DUPLICATE_EXTERNAL_CODE", "external code is unique within bank");
            var invalidMulti = await questions.CreateAsync(input with
            {
                ExternalCode = $"CHECK-MULTI-{suffix}", Type = "MultipleChoice", AnswerValue = "",
                AnswerValues = ["A"], Options = [new("A", "甲", true, 1), new("B", "乙", true, 2)]
            });
            Check(!invalidMulti.IsSuccess && invalidMulti.Code == "INVALID_ANSWER", "multiple choice requires exact answer set");
            var validMulti = await questions.CreateAsync(input with
            {
                ExternalCode = $"CHECK-MULTI-OK-{suffix}", Type = "MultipleChoice", Stem = "哪些属于操作系统？",
                Options = [new("A", "Windows", true, 1), new("B", "Linux", true, 2), new("C", "Word", false, 3)],
                AnswerValue = "", AnswerValues = ["A", "B"]
            });
            Check(validMulti.IsSuccess, "create valid multiple-choice question");
            var validTrueFalse = await questions.CreateAsync(input with
            {
                ExternalCode = $"CHECK-TF-{suffix}", Type = "TrueFalse", Stem = "Windows 是操作系统。",
                Options = [], AnswerValue = "", AnswerValues = [], TrueFalseAnswer = true
            });
            Check(validTrueFalse.IsSuccess, "create valid true-false question");
            var multiDetail = await questions.GetDetailAsync(validMulti.Value!.QuestionId);
            var trueFalseDetail = await questions.GetDetailAsync(validTrueFalse.Value!.QuestionId);
            Check(multiDetail?.AnswerValues.SequenceEqual(["A", "B"]) == true
                && trueFalseDetail is { TrueFalseAnswer: true, Options.Count: 2 },
                "all three question types round-trip their answers");
            var published = await questions.PublishAsync(created.Value!.QuestionId, created.Value!.DraftVersionId, detail.RowVersion);
            Check(published.IsSuccess, "publish draft");
            var readerDetail = await readerQuestions.GetDetailAsync(created.Value!.QuestionId);
            Check(readerDetail is not null && readerDetail.AnswerValue == "" && readerDetail.Options.All(x => !x.IsCorrect),
                "shared-bank readers cannot see answers without write scope");
            await CheckGuardFailureAsync(uow, "UPDATE question_version SET stem = {0} WHERE id = {1}",
                "尝试修改已发布版本", created.Value!.DraftVersionId);
            await CheckGuardFailureAsync(uow, "UPDATE question_option SET content = {0} WHERE question_version_id = {1}",
                "尝试修改已发布选项", created.Value!.DraftVersionId);
            await CheckGuardFailureAsync(uow, "UPDATE question_tag_link SET created_at = created_at WHERE question_version_id = {0}",
                created.Value!.DraftVersionId);
            await CheckGuardFailureAsync(uow, "UPDATE question_category_link SET created_at = created_at WHERE question_version_id = {0}",
                created.Value!.DraftVersionId);
            await CheckGuardFailureAsync(uow, "UPDATE question SET type = {0} WHERE id = {1}",
                "MultipleChoice", created.Value!.QuestionId);
            detail = await questions.GetDetailAsync(created.Value!.QuestionId) ?? throw new InvalidOperationException("发布后题目读取失败。");
            var copied = await questions.CreateDraftVersionAsync(created.Value!.QuestionId, detail.RowVersion);
            Check(copied.IsSuccess && copied.Value!.DraftVersionId != created.Value!.DraftVersionId, "published question copies a new draft");
            detail = await questions.GetDetailAsync(created.Value!.QuestionId) ?? throw new InvalidOperationException("草稿题目读取失败。");
            Check(detail.DraftVersionId == copied.Value!.DraftVersionId && detail.CurrentVersionId == created.Value!.DraftVersionId,
                "draft editing does not replace published current version");
            var updatedDraft = input with { Stem = "下列哪项是操作系统（修订）？", Analysis = "修订解析" };
            Check((await questions.UpdateAsync(created.Value!.QuestionId, updatedDraft, detail.RowVersion)).IsSuccess, "update copied draft");
            detail = await questions.GetDetailAsync(created.Value!.QuestionId) ?? throw new InvalidOperationException("修订题目读取失败。");
            Check((await questions.PublishAsync(created.Value!.QuestionId, copied.Value!.DraftVersionId, detail.RowVersion)).IsSuccess, "publish revised draft");
            detail = await questions.GetDetailAsync(created.Value!.QuestionId) ?? throw new InvalidOperationException("归档前题目读取失败。");
            Check((await questions.ArchiveAsync(created.Value!.QuestionId, detail.RowVersion)).IsSuccess, "archive question");
            await CheckGuardFailureAsync(uow, "UPDATE question SET updated_at = updated_at WHERE id = {0}", created.Value!.QuestionId);

            var template = Path.Combine(environment.WebRootPath, "files", "question-import-template.xlsx");
            await using (var stream = System.IO.File.OpenRead(template))
            {
                var preview = await imports.PreviewAsync(bankId, stream, "题目导入模板.xlsx", "AllOrNothing");
                Check(preview.IsSuccess && preview.Value!.TotalRows > 0 && preview.Value.InvalidRows == 0, "parse and preview real XLSX template");
                var batchId = preview.Value!.BatchId;
                importedFile = (await db.ImportBatches.AsNoTracking().SingleAsync(x => x.Id == batchId)).FileKey;
                var beforeReplay = await db.Questions.CountAsync(x => x.BankId == bankId);
                await using var replayStream = System.IO.File.OpenRead(template);
                var replay = await imports.PreviewAsync(bankId, replayStream, "题目导入模板.xlsx", "AllOrNothing");
                Check(replay.IsSuccess && replay.Value!.Replayed && replay.Value.BatchId == batchId, "same file hash replays original batch");
                Check(await db.Questions.CountAsync(x => x.BankId == bankId) == beforeReplay, "preview and replay create no questions");

                var previewRows = await imports.GetDetailAsync(batchId, 1, 20);
                var occupiedCode = previewRows.Value!.Rows.First(x => !string.IsNullOrWhiteSpace(x.ExternalCode)).ExternalCode!;
                var collision = await questions.CreateAsync(new QuestionEditInput(bankId, occupiedCode, "SingleChoice", "并发占用编码",
                    "Easy", 1m, null, [new("A", "甲", true, 1), new("B", "乙", false, 2)], [], [], "A", [], false));
                Check(collision.IsSuccess, "create concurrent external-code collision fixture");
                var countBeforeRollback = await db.Questions.CountAsync(x => x.BankId == bankId);
                var rolledBack = await imports.ConfirmAsync(bankId, new ImportConfirmInput(batchId, "AllOrNothing", preview.Value.RowVersion, []));
                Check(!rolledBack.IsSuccess && rolledBack.Code == "DUPLICATE_EXTERNAL_CODE", "all-or-nothing reports duplicate code");
                Check(await db.Questions.CountAsync(x => x.BankId == bankId) == countBeforeRollback
                    && (await db.ImportBatches.AsNoTracking().SingleAsync(x => x.Id == batchId)).Status == "Previewed",
                    "all-or-nothing failure rolls back every imported row and restores batch state");
                var collisionQuestion = await db.Questions.SingleAsync(x => x.BankId == bankId && x.ExternalCode == occupiedCode);
                collisionQuestion.ExternalCode = $"CHECK-COLLISION-{suffix}";
                await db.SaveChangesAsync();

                var confirmed = await imports.ConfirmAsync(bankId, new ImportConfirmInput(batchId, "AllOrNothing", preview.Value.RowVersion, []));
                Check(confirmed.IsSuccess && confirmed.Value!.ImportedRows == preview.Value.TotalRows,
                    $"confirm imports every valid row atomically (success={confirmed.IsSuccess}, code={confirmed.Code}, error={confirmed.Error}, imported={confirmed.Value?.ImportedRows}, total={preview.Value.TotalRows}, duplicate={preview.Value.DuplicateRows}, warning={preview.Value.WarningRows})");
                var replayedConfirm = await imports.ConfirmAsync(bankId, new ImportConfirmInput(batchId, "AllOrNothing", preview.Value.RowVersion, []));
                Check(replayedConfirm.IsSuccess && replayedConfirm.Value!.ImportedRows == confirmed.Value!.ImportedRows, "terminal confirmation is idempotent");
                Check(await db.Questions.CountAsync(x => x.BankId == bankId) == countBeforeRollback + preview.Value.TotalRows,
                    "import row count matches created questions");
                var importDetail = await imports.GetDetailAsync(batchId, 1, 20);
                Check(importDetail.IsSuccess && importDetail.Value!.Rows.All(x => string.IsNullOrWhiteSpace(x.Stem)), "terminal import detail hides full stems");
            }
            Check(await db.AuditEvents.CountAsync(x => x.Action.StartsWith("question.")) > 0, "question writes emit database audit");
            }, commit: false);
            Console.WriteLine("Phase 2 self-check passed: banks, categories, tags, three-type validation, immutable publication, draft copies, XLSX preview/replay/confirmation and audit rolled back.");
        }
        finally
        {
            httpContextAccessor.HttpContext = null;
            if (importedFile is not null) storage.DeleteImport(importedFile);
        }
    }

    private static async Task CheckGuardFailureAsync(IUnitOfWork uow, string sql, params object[] parameters)
    {
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                // 直接走 ADO.NET：这些 23514 是自查预期结果，不应被 EF 命令日志记成业务 Error。
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
                await command.ExecuteNonQueryAsync();
            });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return;
        }
        throw new InvalidOperationException($"Phase 2 self-check failed: database guard accepted {sql}");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Phase 2 self-check failed: {name}");
    }

    private sealed class ScopeCheckUser(
        IReadOnlyList<RoleAssignmentData> assignments,
        long userId,
        string username) : ICurrentUser
    {
        private readonly string[] _permissions = assignments
            .SelectMany(x => x.Permissions).Distinct(StringComparer.Ordinal).ToArray();

        public long? Id => userId;
        public string? DisplayName => username;
        public string? Username => username;
        public bool IsAuthenticated => true;
        public bool MustChangePassword => false;
        public IReadOnlyList<RoleAssignmentData> Roles => assignments;
        public IReadOnlyList<DepartmentAccessData> Departments => [];
        public Task LoadAsync() => Task.CompletedTask;
        public bool HasPermission(string permission) => _permissions.Contains(permission, StringComparer.Ordinal);
        public bool HasAnyPermission(params string[] permissions) => permissions.Any(HasPermission);
        public bool HasRole(string roleCode) => assignments.Any(x => x.Code == roleCode);
        public AuditContext GetAuditContext() => new(userId,
            string.Join(',', assignments.Select(x => x.Code).Distinct()), "phase2-check", "127.0.0.1", "ExamOps.Check");
    }
}
