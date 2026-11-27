using System.Text.Json;
using ExamOps.DAL;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamOps.DAL;

public interface IUnitOfWork
{
    ExamDbContext Db { get; }
    Task<int> SaveChangesAsync();
    Task ExecuteInTransactionAsync(Func<ExamDbContext, Task> action, bool commit = true);
}

public sealed class UnitOfWork(IDbContextFactory<ExamDbContext> factory, ILoggerFactory loggerFactory) : IUnitOfWork, IDisposable, IAsyncDisposable
{
    private ExamDbContext? _db;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<AuditEvent> _pendingAudits = [];
    private readonly ILogger _auditLogger = loggerFactory.CreateLogger("Audit");

    public ExamDbContext Db => _db ??= factory.CreateDbContext();

    public async Task<int> SaveChangesAsync()
    {
        var added = Db.ChangeTracker.Entries<AuditEvent>()
            .Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray();
        var result = await Db.SaveChangesAsync();
        if (Db.Database.CurrentTransaction is null) WriteAuditFile(added);
        return result;
    }

    public async Task ExecuteInTransactionAsync(Func<ExamDbContext, Task> action, bool commit = true)
    {
        if (Db.Database.CurrentTransaction is not null)
        {
            // 自查或组合业务可能嵌在外层事务中；用 savepoint 隔离内层失败，避免污染外层已写入的数据。
            var transaction = Db.Database.CurrentTransaction!;
            await transaction.CreateSavepointAsync("examops_nested");
            try
            {
                await action(Db);
                await transaction.ReleaseSavepointAsync("examops_nested");
            }
            catch
            {
                await transaction.RollbackToSavepointAsync("examops_nested");
                // EF 已在失败前 AcceptAllChanges；清空跟踪，确保下一次查询从回滚后的数据库重新加载。
                Db.ChangeTracker.Clear();
                throw;
            }
            return;
        }
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _pendingAudits.Clear();
            // 每次重试使用新上下文，避免复用已递增的 RowVersion、审计前像和实体跟踪状态。
            await using var db = await factory.CreateDbContextAsync();
            var previousDb = _db;
            _db = db;
            try
            {
                db.SavingChanges += (_, _) => _pendingAudits.AddRange(db.ChangeTracker.Entries<AuditEvent>()
                    .Where(x => x.State == EntityState.Added).Select(x => x.Entity));
                await using var transaction = await db.Database.BeginTransactionAsync();
                await action(db);
                if (commit)
                {
                    await transaction.CommitAsync();
                    WriteAuditFile(_pendingAudits);
                }
                else
                {
                    await transaction.RollbackAsync();
                }
            }
            finally
            {
                // 调用方在事务前创建的上下文仍由作用域末尾释放，不能提前丢弃。
                _db = previousDb;
            }
        });
    }

    private void WriteAuditFile(IEnumerable<AuditEvent> audits)
    {
        try
        {
            foreach (var audit in audits)
            {
                // 结构化 JSON 交给日志提供程序追加到 Data/logs/audit；仅在数据库提交成功后调用。
                _auditLogger.LogInformation("Audit committed: {AuditEvent}", JsonSerializer.Serialize(audit, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            // 数据库已经提交，日志故障不能把业务结果误报为失败并触发重试。
            try { _auditLogger.LogError(ex, "数据库已提交，但审计文件输出失败。AuditId={AuditId}", audits.LastOrDefault()?.Id); }
            catch (Exception fallbackEx) when (fallbackEx is IOException or UnauthorizedAccessException)
            {
                // 降级保护：次级日志通道同时不可写时仍保证已提交事务正常返回。
            }
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not { } db) return;
        _db = null;
        await db.DisposeAsync();
    }
}
