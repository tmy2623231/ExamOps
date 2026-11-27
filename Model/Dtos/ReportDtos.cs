namespace ExamOps.Model.Dtos;

public static class ReportTypes
{
    public const string Summary = "Summary";
    public const string Distribution = "Distribution";
    public const string PassRate = "PassRate";
    public const string DepartmentCompare = "DepartmentCompare";
    public const string Questions = "Questions";
    public const string Anomalies = "Anomalies";
    public const string Results = "Results";
}

public enum ReportColumnKind { Text, Integer, Decimal, Percent, DateTime }

public sealed record ReportColumn(string Key, string Label, ReportColumnKind Kind = ReportColumnKind.Text);

public sealed record ReportRow(IReadOnlyDictionary<string, object?> Values);

public sealed record ReportTable(
    string Title,
    DateTime GeneratedAt,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<ReportRow> Rows,
    string? Note = null);

public sealed record DepartmentCompareQuery(long? TaskId, DateTime? From, DateTime? To);

public sealed record ReportQuery(int PageIndex = 1, int PageSize = 50);

public sealed record AnomalyRow(
    long Id,
    string Source,
    long AttemptId,
    int AttemptNo,
    long UserId,
    string UserName,
    string EventType,
    string Reason,
    long? ActorId,
    string? ActorName,
    DateTime OccurredAt);

public sealed record ExportReportInput(string ReportType, string Format);

public sealed record ExportReportOutput(
    long ExportId,
    string FileName,
    string ContentType,
    string DownloadUrl,
    DateTime ExpiresAt,
    int RowCount);

public sealed record ReportTaskOption(long Id, string Title, string ResultStatus);

