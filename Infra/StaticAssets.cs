namespace ExamOps.Infra;

public static class StaticAssets
{
    public static string Version { get; } = typeof(StaticAssets).Assembly.ManifestModule.ModuleVersionId.ToString("N");
}
