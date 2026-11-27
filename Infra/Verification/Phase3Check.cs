using System.Security.Claims;
using System.Text.RegularExpressions;
using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ExamOps.Infra.Verification;

public sealed class Phase3Check(IServiceScopeFactory scopeFactory, IHttpContextAccessor httpContextAccessor, IClock clock)
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
            [new("examops_user_id", admin.Id.ToString()), new(ClaimTypes.Name, "admin")], "ExamOps.Check"))
        };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        httpContextAccessor.HttpContext = context;
        try
        {
            await uow.ExecuteInTransactionAsync(async _ =>
            {
                var db = uow.Db;
                var account = await db.Users.SingleAsync(x => x.Id == admin.Id);
                if (account.Status != "Active") { account.Status = "Active"; account.RowVersion++; await db.SaveChangesAsync(); }
                var currentUser = services.GetRequiredService<ICurrentUser>();
                await currentUser.LoadAsync();
                var root = await db.Departments.AsNoTracking().SingleAsync(x => x.Path == "001.");
                var suffix = clock.UtcNow.Ticks.ToString();
                var questions = services.GetRequiredService<QuestionService>();
                var papers = services.GetRequiredService<PaperService>();
                var bank = await questions.CreateBankAsync($"组卷自查题库-{suffix}", root.Id, true);
                Check(bank.IsSuccess, "create paper-check bank");
                var category = await questions.CreateCategoryAsync(bank.Value!, "组卷分类", null);
                Check(category.IsSuccess, "create paper-check category");
                var versionIds = new List<long>();
                for (var i = 1; i <= 3; i++)
                {
                    var created = await questions.CreateAsync(new QuestionEditInput(bank.Value!, $"PAPER-CHECK-{suffix}-{i}",
                        i == 2 ? "MultipleChoice" : i == 3 ? "TrueFalse" : "SingleChoice",
                        $"组卷自查题目 {i}", i == 3 ? "Hard" : "Easy", 5m, null,
                        i == 3 ? [] : [new("A", "甲", i != 3, 1), new("B", "乙", i == 2, 2)],
                        [], [category.Value!], i == 2 ? "" : i == 3 ? "" : "A", i == 2 ? ["A", "B"] : [], i == 3));
                    Check(created.IsSuccess, $"create paper-check question {i}");
                    var questionDetail = await questions.GetDetailAsync(created.Value!.QuestionId);
                    var questionPublished = await questions.PublishAsync(created.Value.QuestionId, created.Value.DraftVersionId, questionDetail!.RowVersion);
                    Check(questionPublished.IsSuccess, $"publish paper-check question {i}");
                    versionIds.Add(created.Value.DraftVersionId);
                }

                var createInput = new CreatePaperInput($"组卷自查试卷-{suffix}", root.Id, 60);
                var api = new ApiAuditMetadata($"phase3-create-{suffix}", "hash-a", "POST", "/api/papers/{paperId}");
                var createdPaper = await papers.CreateAsync(createInput, api);
                Check(createdPaper.IsSuccess, "create paper draft and v1");
                var replayed = await papers.CreateAsync(createInput, api);
                Check(replayed.IsSuccess && replayed.Value == createdPaper.Value, "same idempotency key replays the same paper");
                var conflictingReplay = await papers.CreateAsync(createInput with { Title = $"幂等冲突-{suffix}" }, api with { RequestHash = "hash-b" });
                Check(!conflictingReplay.IsSuccess && conflictingReplay.Code == "IDEMPOTENCY_CONFLICT", "same idempotency key rejects a different payload under the lock");
                Check(await db.Papers.CountAsync(x => x.Title == createInput.Title) == 1, "idempotent create has one side effect");
                var paperId = createdPaper.Value!.PaperId;
                var versionId = createdPaper.Value.VersionId;
                var detail = await papers.GetDetailAsync(paperId, versionId) ?? throw new InvalidOperationException("试卷草稿读取失败。");
                var fixedSection = new PaperSectionInput(0, "固定题", 1, "Fixed", null, 5m, null, null, null, null, versionIds.Take(2).ToArray());
                var duplicateSection = fixedSection with { Title = "重复固定题", SortOrder = 2, QuestionVersionIds = [versionIds[0]] };
                var duplicateUpdate = await papers.UpdateDraftAsync(paperId, versionId, detail.RowVersion, detail.VersionRowVersion,
                    new(detail.VersionTitle, 60, [fixedSection, duplicateSection]));
                Check(duplicateUpdate.IsSuccess, "draft stores overlapping fixed sections for preview");
                var overLimit = await papers.UpdateDraftAsync(paperId, versionId, detail.RowVersion, detail.VersionRowVersion,
                    new(detail.VersionTitle, 60, [new PaperSectionInput(0, "超分随机题", 1, "Random", 200, 1000m, bank.Value, category.Value, null, null, [])]));
                Check(!overLimit.IsSuccess && overLimit.Code == "TOTAL_SCORE_TOO_HIGH", "aggregate score above database numeric limit is rejected before writing");
                detail = await papers.GetDetailAsync(paperId, versionId) ?? throw new InvalidOperationException("试卷读取失败。");
                Check(detail.Preview.TotalScore == 15m && detail.Preview.DuplicateQuestionCount == 1
                    && detail.Preview.Issues.Any(x => x.Code == "FIXED_DUPLICATE"), "fixed total and duplicate preview are exact");
                var blocked = await papers.PublishAsync(paperId, new(versionId, detail.VersionRowVersion), detail.RowVersion);
                Check(!blocked.IsSuccess && blocked.Code == "FIXED_DUPLICATE", "duplicate fixed questions block publication");

                var randomSection = new PaperSectionInput(0, "随机题", 2, "Random", 1, 5m, bank.Value, category.Value, null, null, []);
                detail = await papers.GetDetailAsync(paperId, versionId) ?? throw new InvalidOperationException("试卷读取失败。");
                var saved = await papers.UpdateDraftAsync(paperId, versionId, detail.RowVersion, detail.VersionRowVersion,
                    new(detail.VersionTitle, 60, [fixedSection, randomSection]));
                Check(saved.IsSuccess, "save fixed and blueprint random sections");
                detail = await papers.GetDetailAsync(paperId, versionId) ?? throw new InvalidOperationException("试卷读取失败。");
                Check(detail.Preview.TotalScore == 15m && detail.Preview.SelectedRandomCount == 1
                    && detail.Preview.Issues.Any(x => x.Code == "POOL_SHORT"), "random score is conserved before pool generation");
                var generated = await papers.ApplyBlueprintAsync(paperId, versionId, detail.Sections.Single(x => x.SelectionMode == "Random").Id,
                    detail.RowVersion, detail.VersionRowVersion);
                Check(generated.IsSuccess, "server generates a persisted random candidate pool");
                detail = await papers.GetDetailAsync(paperId, versionId) ?? throw new InvalidOperationException("试卷读取失败。");
                Check(detail.Preview.RandomPoolCount == 3 && detail.Preview.TotalScore == 15m
                    && detail.Preview.Issues.All(x => x.Code != "POOL_SHORT"), "random pool count and score preview");
                var published = await papers.PublishAsync(paperId, new(versionId, detail.VersionRowVersion), detail.RowVersion);
                Check(published.IsSuccess, "publish validated paper version");
                detail = await papers.GetDetailAsync(paperId, versionId) ?? throw new InvalidOperationException("试卷读取失败。");
                Check(detail.Status == "Published" && detail.VersionStatus == "Published" && detail.Versions.Single(x => x.Id == detail.VersionId).IsCurrent, "paper points to published version");
                await CheckGuardFailureAsync(uow, "UPDATE paper_section SET title = {0} WHERE id = {1}", "篡改", detail.Sections[0].Id);
                await CheckGuardFailureAsync(uow, "DELETE FROM paper_question WHERE id = {0}", detail.Questions[0].Id);

                var copy = await papers.CopyDraftAsync(paperId, detail.RowVersion);
                Check(copy.IsSuccess && copy.Value!.VersionId != versionId, "copy published version into a new draft");
                detail = await papers.GetDetailAsync(paperId) ?? throw new InvalidOperationException("试卷读取失败。");
                Check(detail.CurrentVersionId == versionId && detail.DraftVersionId == copy.Value!.VersionId
                    && detail.VersionId == copy.Value.VersionId, "revision draft does not replace published current version");
                var childResult = await services.GetRequiredService<OrganizationService>()
                    .CreateDepartmentAsync($"组卷范围自查-{suffix}", root.Id);
                Check(childResult.IsSuccess, "create scope fixture department");
                var child = await db.Departments.AsNoTracking().SingleAsync(x => x.Name == $"组卷范围自查-{suffix}");
                var deniedUser = new ScopeCheckUser([new(RoleCodes.QuestionManager, child.Id, "Department",
                    [Permissions.PaperCreate, Permissions.PaperUpdate, Permissions.PaperRead])], admin.Id, "scope-denied");
                var deniedPapers = new PaperService(uow, services.GetRequiredService<PaperRepository>(), services.GetRequiredService<QuestionRepository>(),
                    new DataScopeService(deniedUser, services.GetRequiredService<DepartmentRepository>(), uow), services.GetRequiredService<AuditRepository>(), deniedUser, clock);
                var denied = await deniedPapers.CreateAsync(createInput with { Title = $"越权试卷-{suffix}" });
                Check(!denied.IsSuccess && denied.Code == "OUT_OF_SCOPE", "paper department scope denies parent-department write");
                var archived = await papers.ArchiveAsync(paperId, detail.RowVersion);
                Check(archived.IsSuccess, "archive paper");
                detail = await papers.GetDetailAsync(paperId) ?? throw new InvalidOperationException("试卷读取失败。");
                Check(detail.Status == "Archived" && detail.VersionStatus == "Archived"
                    && detail.Versions.All(x => x.Status == "Archived"), "archive freezes published history and every revision draft");
                var archivedDraftSectionId = await db.PaperSections.AsNoTracking().Where(x => x.PaperVersionId == copy.Value!.VersionId)
                    .Select(x => x.Id).FirstAsync();
                await CheckGuardFailureAsync(uow, "UPDATE paper_section SET title = {0} WHERE id = {1}", "篡改归档草稿", archivedDraftSectionId);
                Check(await db.AuditEvents.CountAsync(x => x.Action.StartsWith("paper.")) >= 7, "paper writes share database audit transaction");
            }, commit: false);
            Console.WriteLine("Phase 3 self-check passed: fixed/random blueprint, totals, duplicate guard, pool generation, immutable publication, draft copy, archive, scope, audit and idempotent create rolled back.");
        }
        finally { httpContextAccessor.HttpContext = null; }
    }

    private static async Task CheckGuardFailureAsync(IUnitOfWork uow, string sql, params object[] parameters)
    {
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
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
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation) { return; }
        throw new InvalidOperationException($"Phase 3 self-check failed: database guard accepted {sql}");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Phase 3 self-check failed: {name}");
    }

    private sealed class ScopeCheckUser(IReadOnlyList<RoleAssignmentData> assignments, long userId, string username) : ICurrentUser
    {
        private readonly string[] _permissions = assignments.SelectMany(x => x.Permissions).Distinct(StringComparer.Ordinal).ToArray();
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
        public AuditContext GetAuditContext() => new(userId, string.Join(',', assignments.Select(x => x.Code)), "phase3-check", "127.0.0.1", "ExamOps.Check");
    }
}
