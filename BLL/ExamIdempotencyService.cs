using System.Text.Json;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using Microsoft.EntityFrameworkCore;
using ExamOps.Model.Dtos;

namespace ExamOps.BLL;

public sealed class ExamIdempotencyService(ICurrentUser currentUser, AuditRepository audit)
{
    public async Task<T?> FindReplayAsync<T>(ExamDbContext db, ApiAuditMetadata? api)
    {
        if (api is null || string.IsNullOrWhiteSpace(api.IdempotencyKey)) return default;
        await LockAsync(db, api);
        var actorId = currentUser.Id ?? throw new InvalidOperationException("认证用户缺少 ID");
        var existing = await audit.FindApiAuditAsync(db, actorId, api);
        if (existing is null) return default;
        if (!string.Equals(existing.RequestHash, api.RequestHash, StringComparison.Ordinal)) throw new ExamIdempotencyConflictException();
        return JsonSerializer.Deserialize<T>(existing.ResultRefJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public AuditContext BuildAuditContext(ApiAuditMetadata? api, object resultRef)
    {
        var context = currentUser.GetAuditContext();
        return api is null ? context with { ResultRef = resultRef } : context with
        {
            IdempotencyKey = api.IdempotencyKey,
            RequestHash = api.RequestHash,
            HttpMethod = api.HttpMethod,
            RouteTemplate = api.RouteTemplate,
            ResultRef = resultRef
        };
    }

    private async Task LockAsync(ExamDbContext db, ApiAuditMetadata api)
    {
        var actorId = currentUser.Id ?? throw new InvalidOperationException("认证用户缺少 ID");
        var key = $"{actorId}|{api.HttpMethod}|{api.RouteTemplate}|{api.IdempotencyKey}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))");
    }
}

public sealed class ExamIdempotencyConflictException : Exception;
