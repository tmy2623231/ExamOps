namespace ExamOps.Model.Entities;

public sealed class Notification
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
