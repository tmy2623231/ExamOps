namespace ExamOps.Model.Entities;

public sealed class PaperSection
{
    public long Id { get; set; }
    public long PaperVersionId { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; } = 1;
    public string SelectionMode { get; set; } = "Fixed";
    public int? PickCount { get; set; }
    public decimal ScorePerQuestion { get; set; } = 1;
    public long? BankId { get; set; }
    public long? CategoryId { get; set; }
    public string? QuestionType { get; set; }
    public string? Difficulty { get; set; }
    public long RowVersion { get; set; } = 1;
}
