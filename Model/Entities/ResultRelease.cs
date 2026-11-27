namespace ExamOps.Model.Entities;

public sealed class ResultRelease
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public int ReleaseNo { get; set; }
    public string ScorePolicy { get; set; } = "Latest";
    public decimal? PassScore { get; set; }
    public string ResultReleasePolicy { get; set; } = "Manual";
    public string SummaryJson { get; set; } = "{}";
    public string PayloadSha256 { get; set; } = "";
    public long? PublishedBy { get; set; }
    public DateTime PublishedAt { get; set; }
    public string IdempotencyKey { get; set; } = "";
}

public sealed class ResultReleaseItem
{
    public long Id { get; set; }
    public long ReleaseId { get; set; }
    public long TaskId { get; set; }
    public long AssignmentId { get; set; }
    public long UserId { get; set; }
    public long DepartmentIdSnapshot { get; set; }
    public long? AttemptId { get; set; }
    public decimal? FinalScore { get; set; }
    public decimal? MaxScore { get; set; }
    public bool? Passed { get; set; }
    public string SourceJson { get; set; } = "{}";
}

public sealed class ReportExport
{
    public long Id { get; set; }
    public long? TaskId { get; set; }
    public string ReportType { get; set; } = "";
    public string Format { get; set; } = "CSV";
    public string FileKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public int RowCount { get; set; }
    public string FilterJson { get; set; } = "{}";
    public long CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
