namespace ExamOps.Infra;

public static class StaticAssets
{
    public static string Version { get; } = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
}
