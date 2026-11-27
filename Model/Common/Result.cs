namespace ExamOps.Model.Common;

public record Result
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public string? Code { get; init; }

    public static Result Ok() => new() { IsSuccess = true };
    public static Result Fail(string error, string? code = null)
        => new() { IsSuccess = false, Error = error, Code = code };
}

public sealed record Result<T> : Result
{
    public T? Value { get; init; }

    public static Result<T> Ok(T value) => new() { IsSuccess = true, Value = value };
    public new static Result<T> Fail(string error, string? code = null)
        => new() { IsSuccess = false, Error = error, Code = code };
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int PageIndex, int PageSize);
