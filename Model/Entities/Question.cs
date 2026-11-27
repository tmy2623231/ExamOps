namespace ExamOps.Model.Entities;

public sealed class Question
{
    public long Id { get; set; }
    public long BankId { get; set; }
    public string? ExternalCode { get; set; }
    public string Type { get; set; } = "SingleChoice";
    public string Status { get; set; } = "Draft";
    public long? CurrentVersionId { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}
