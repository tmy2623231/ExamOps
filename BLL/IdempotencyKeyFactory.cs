using System.Security.Cryptography;
using System.Text;
using ExamOps.Model.Dtos;

namespace ExamOps.BLL;

internal static class IdempotencyKeyFactory
{
    public static string Create(ICurrentUser currentUser, ApiAuditMetadata? api, string operation)
        => Create(currentUser.Id, api, operation);

    internal static string Create(long? actorId, ApiAuditMetadata? api, string operation)
    {
        if (string.IsNullOrWhiteSpace(api?.IdempotencyKey))
            return $"{operation}:{Guid.NewGuid():N}";

        if (actorId is null) throw new InvalidOperationException("认证用户缺少 ID");
        var raw = $"{actorId}|{api.HttpMethod}|{api.RouteTemplate}|{operation}|{api.IdempotencyKey}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32].ToLowerInvariant();
        return $"{operation}:{hash}";
    }
}
