namespace ExamOps.Model.Dtos;

public sealed record AuditContext(long? ActorId, string? ActorRole, string? RequestId, string? Ip, string? UserAgent)
{
    public string? IdempotencyKey { get; init; }
    public string? RequestHash { get; init; }
    public string? HttpMethod { get; init; }
    public string? RouteTemplate { get; init; }
    public object? ResultRef { get; init; }
}
