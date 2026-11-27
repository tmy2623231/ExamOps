namespace ExamOps.Model.Entities;

public sealed class PaperQuestion
{
    public long Id { get; set; }
    public long PaperVersionId { get; set; }
    public long SectionId { get; set; }
    public long QuestionVersionId { get; set; }
    public int SortOrder { get; set; } = 1;
    public decimal Score { get; set; } = 1;
    public bool IsPool { get; set; }
    public long RowVersion { get; set; } = 1;
}
