namespace ExamOps.Model.Entities;

public sealed class QuestionVersion
{
    public long Id { get; set; }
    public long QuestionId { get; set; }
    public int VersionNo { get; set; }
    public string Stem { get; set; } = "";
    public string AnswerJson { get; set; } = "{}";
    public string Difficulty { get; set; } = "Medium";
    public decimal DefaultScore { get; set; } = 1;
    public string? Analysis { get; set; }
    public string Status { get; set; } = "Draft";
    public string Source { get; set; } = "Manual";
    public long? ImportBatchId { get; set; }
    public long? ImportRowId { get; set; }
    public DateTime? PublishedAt { get; set; }
    public long? PublishedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}
