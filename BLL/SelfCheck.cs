using ExamOps.Infra.Background;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamOps.BLL;

public sealed class SelfCheck(IHostEnvironment environment, IClock clock)
{
    private int _checks;

    public void Run()
    {
        Check(DepartmentPath.BuildChildPath("", []) == "001.", "root department path");
        Check(DepartmentPath.BuildChildPath("001.", []) == "001.001.", "first child path");
        Check(DepartmentPath.BuildChildPath("001.001.", [1, 3]) == "001.001.004.", "next sibling path");
        Check(AnswerValidator.Grade("SingleChoice", ["A"], ["A"]).IsCorrect, "single choice correct");
        Check(!AnswerValidator.Grade("SingleChoice", ["B"], ["A"]).IsCorrect, "single choice wrong");
        Check(!AnswerValidator.Grade("MultipleChoice", ["A"], ["A", "B"]).IsCorrect, "multiple choice has no partial credit");
        Check(AnswerValidator.Grade("MultipleChoice", ["B", "A", "A"], ["A", "B"]).IsCorrect, "multiple choice exact set");
        Check(AnswerValidator.Grade("TrueFalse", ["正确"], ["A"]).IsCorrect, "judgment maps 正确 to A");
        Check(!AnswerValidator.Grade("FillBlank", ["x"], ["x"]).IsValid, "unsupported type rejected");
        Check(DataScopeRules.IsValidScopeType("All") && DataScopeRules.IsValidScopeType("Self"), "valid role scopes");
        Check(DataScopeRules.RequiresDepartmentAnchor("DepartmentSubtree"), "department scope requires anchor");
        Check(!DataScopeRules.RequiresDepartmentAnchor("Assigned"), "assigned scope has no department anchor");
        Check(DataScopeRules.MatchesDepartment("Department", 2, "001.002.", 2, "001.002.")
            && !DataScopeRules.MatchesDepartment("Department", 3, "001.002.001.", 2, "001.002."),
            "department scope excludes children");
        Check(DataScopeRules.MatchesDepartment("DepartmentSubtree", 3, "001.002.001.", 2, "001.002."),
            "department subtree scope includes children");
        CheckIdempotencyKeys();
        CheckCleanup();
        Console.WriteLine($"Self-check passed: {_checks} assertions.");
    }

    private void CheckIdempotencyKeys()
    {
        var api = new ApiAuditMetadata("same-key", "hash", "POST", "/api/attempts/{attemptId}/review");
        var first = IdempotencyKeyFactory.Create(1, api, "attempt-review:10");
        Check(first == IdempotencyKeyFactory.Create(1, api, "attempt-review:10"), "idempotency key is stable");
        Check(first != IdempotencyKeyFactory.Create(2, api, "attempt-review:10"), "idempotency key is actor scoped");
        Check(first != IdempotencyKeyFactory.Create(1, api, "attempt-review:11"), "idempotency key is resource scoped");
        Check(first.Length <= 128, "idempotency key fits database column");
    }

    private void CheckCleanup()
    {
        var root = Path.Combine(environment.ContentRootPath, "Data", "tmp", "cleanup-self-check");
        var tmp = Path.Combine(root, "tmp");
        var export = Path.Combine(root, "export");
        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(export);
        var oldFile = Path.Combine(tmp, "old.tmp");
        var currentFile = Path.Combine(export, "current.tmp");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(currentFile, "current");
        File.SetLastWriteTimeUtc(oldFile, clock.UtcNow.AddHours(-3));
        try
        {
            using var cleanup = new DataCleanupService(Path.Combine(root, "logs"), export, tmp,
                30, 24, 1, clock, NullLogger<DataCleanupService>.Instance);
            cleanup.CleanupAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(!File.Exists(oldFile) && File.Exists(currentFile), "retention removes only expired files");
        }
        finally { Directory.Delete(root, true); }
    }

    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Self-check failed: {name}");
        _checks++;
    }
}
