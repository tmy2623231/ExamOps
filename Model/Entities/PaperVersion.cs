namespace ExamOps.Model.Entities;

public sealed class PaperVersion
{
    public long Id { get; set; }
    public long PaperId { get; set; }
    public int VersionNo { get; set; }
    public string Title { get; set; } = "";
    public decimal TotalScore { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public string Status { get; set; } = "Draft";
    public DateTime? PublishedAt { get; set; }
    public long? PublishedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}
