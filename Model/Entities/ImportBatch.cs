namespace ExamOps.Model.Entities;

public sealed class ImportBatch
{
    public long Id { get; set; }
    public long BankId { get; set; }
    public string FileName { get; set; } = "";
    public string FileKey { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string Mode { get; set; } = "AllOrNothing";
    public string Status { get; set; } = "Uploaded";
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public int WarningRows { get; set; }
    public int DuplicateRows { get; set; }
    public int ImportedRows { get; set; }
    public int FailedRows { get; set; }
    public int ExcludedRows { get; set; }
    public string WarningJson { get; set; } = "[]";
    public DateTime? ConfirmedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}
