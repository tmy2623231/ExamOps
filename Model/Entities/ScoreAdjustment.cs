namespace ExamOps.Model.Entities;

public sealed class ScoreAdjustment
{
    public long Id { get; set; }
    public long AttemptId { get; set; }
    public decimal OldScore { get; set; }
    public decimal NewScore { get; set; }
    public string Reason { get; set; } = "";
    public long AdjustedBy { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
