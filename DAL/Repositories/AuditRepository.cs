using System.Text.Json;
using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
namespace ExamOps.DAL.Repositories;

public sealed class AuditRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task WriteAsync(ExamDbContext db, IClock clock, AuditContext? context, string action,
        string entityType, long entityId, object? before = null, object? after = null, string? reason = null,
        string? eventKey = null)
    {
        var now = clock.UtcNow;
        var beforeJson = Serialize(before);
        var afterJson = Serialize(after);
        db.AuditEvents.Add(new AuditEvent
        {
            ActorId = context?.ActorId,
            ActorRole = context?.ActorRole,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Reason = reason,
            RequestId = context?.RequestId,
            TraceId = context?.RequestId,
            IdempotencyKey = context?.IdempotencyKey,
            RequestHash = context?.RequestHash,
            HttpMethod = context?.HttpMethod,
            RouteTemplate = context?.RouteTemplate,
            ResultRefJson = Serialize(context?.ResultRef),
            EventKey = eventKey,
            Ip = context?.Ip,
            UserAgent = context?.UserAgent,
            CreatedAt = now
        });
        // 文件审计由 UnitOfWork 在数据库提交成功后输出，回滚不会留下“成功”记录。
        return Task.CompletedTask;
    }

    public async Task<AuditEvent?> FindApiAuditAsync(ExamDbContext db, long actorId, ApiAuditMetadata api)
    {
        if (string.IsNullOrWhiteSpace(api.IdempotencyKey)) return null;
        return await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actorId
            && x.HttpMethod == api.HttpMethod && x.RouteTemplate == api.RouteTemplate && x.IdempotencyKey == api.IdempotencyKey);
    }

    public async Task<T?> FindApiResultAsync<T>(ExamDbContext db, long actorId, ApiAuditMetadata api)
    {
        var row = await FindApiAuditAsync(db, actorId, api);
        return row is null ? default : JsonSerializer.Deserialize<T>(row.ResultRefJson, JsonOptions);
    }

    private static string Serialize(object? value)
        => value is null ? "{}" : JsonSerializer.Serialize(value, JsonOptions);
}
