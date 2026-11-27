namespace ExamOps.Model.Entities;

public sealed class QuestionCategoryLink
{
    public long Id { get; set; }
    public long QuestionVersionId { get; set; }
    public long CategoryId { get; set; }
    public DateTime CreatedAt { get; set; }
}
