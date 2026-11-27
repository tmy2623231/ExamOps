namespace ExamOps.Model.Entities;

public sealed class Paper
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public long DepartmentId { get; set; }
    public string Status { get; set; } = "Draft";
    public long? CurrentVersionId { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}
