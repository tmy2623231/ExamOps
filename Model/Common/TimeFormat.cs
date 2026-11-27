namespace ExamOps.Model.Common;

public static class TimeFormat
{
    public static string Display(DateTime? value)
        => value is null ? "—" : Display(value.Value);

    public static string Display(DateTime value)
        => ToLocal(value).ToString("yyyy-MM-dd HH:mm");

    public static DateTime ToLocal(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
}
