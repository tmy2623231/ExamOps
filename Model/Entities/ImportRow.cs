namespace ExamOps.Model.Entities;

public sealed class ImportRow
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public int RowNo { get; set; }
    public string? ExternalCode { get; set; }
    public string Status { get; set; } = "Valid";
    public string RawJson { get; set; } = "{}";
    public string? AnswerSummary { get; set; }
    public string ErrorJson { get; set; } = "[]";
    public string WarningJson { get; set; } = "[]";
    public long? QuestionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}
