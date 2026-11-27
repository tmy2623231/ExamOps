namespace ExamOps.Infra;

public static class PathResolver
{
    public static string ResolveRoot(string contentRoot, string? configured, string fallback)
        => Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured!
            : Path.Combine(contentRoot, string.IsNullOrWhiteSpace(configured) ? fallback : configured));
}
