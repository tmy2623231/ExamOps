namespace ExamOps.Model.Entities;

public sealed class QuestionOption
{
    public long Id { get; set; }
    public long QuestionVersionId { get; set; }
    public string Label { get; set; } = "A";
    public string Content { get; set; } = "";
    public bool IsCorrect { get; set; }
    public int SortOrder { get; set; } = 1;
}
