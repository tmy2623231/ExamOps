namespace ExamOps.Model.Entities;

public sealed class QuestionBank
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long DepartmentId { get; set; }
    public bool IsShared { get; set; }
    public string Status { get; set; } = "Active";
    public long? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}
