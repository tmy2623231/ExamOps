namespace ExamOps.Model.Entities;

public sealed class QuestionCategory
{
    public long Id { get; set; }
    public long BankId { get; set; }
    public long? ParentId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}
