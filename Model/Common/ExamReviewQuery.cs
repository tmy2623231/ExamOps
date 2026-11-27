namespace ExamOps.Model.Common;

public sealed record ExamReviewQuery
{
    public long? TaskId { get; init; }
    public long? AttemptId { get; init; }
    public string? ReviewType { get; init; }
    public int PageIndex { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
