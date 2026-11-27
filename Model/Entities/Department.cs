namespace ExamOps.Model.Entities;

public sealed class Department
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public Department? Parent { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string PathSegment { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}
