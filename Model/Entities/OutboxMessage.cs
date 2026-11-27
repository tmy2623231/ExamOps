namespace ExamOps.Model.Entities;

public sealed class OutboxMessage
{
    public long Id { get; set; }
    public string EventKey { get; set; } = "";
    public string Type { get; set; } = "";
    public string AggregateType { get; set; } = "";
    public long AggregateId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public int RetryCount { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LockedAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public long RowVersion { get; set; } = 1;
}
