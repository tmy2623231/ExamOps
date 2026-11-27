namespace ExamOps.Model.Entities;

public sealed class QuestionTagLink
{
    public long Id { get; set; }
    public long QuestionVersionId { get; set; }
    public long TagId { get; set; }
    public DateTime CreatedAt { get; set; }
}
