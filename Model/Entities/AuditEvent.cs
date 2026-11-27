using System.Text.Json.Serialization;

namespace ExamOps.Model.Entities;

public sealed class AuditEvent
{
    public long Id { get; set; }
    public long? ActorId { get; set; }
    [JsonIgnore] public AppUser? Actor { get; set; }
    public string? ActorRole { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public long EntityId { get; set; }
    public string BeforeJson { get; set; } = "{}";
    public string AfterJson { get; set; } = "{}";
    public string? Reason { get; set; }
    public string? RequestId { get; set; }
    public string? TraceId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? RequestHash { get; set; }
    public string? HttpMethod { get; set; }
    public string? RouteTemplate { get; set; }
    public string ResultRefJson { get; set; } = "{}";
    public string? EventKey { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}
