using ExamOps.Model.Common;

namespace ExamOps.Infra.Background;

public sealed class DataCleanupService(
    string logRoot,
    string exportRoot,
    string tmpRoot,
    int logDays,
    int exportHours,
    int tmpHours,
    IClock clock,
    ILogger<DataCleanupService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_loop is not null) await _loop.WaitAsync(cancellationToken);
        Dispose();
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await CleanupAsync(cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await CleanupAsync(cancellationToken);
    }

    public Task CleanupAsync(CancellationToken cancellationToken)
        => Task.Run(() => Cleanup(cancellationToken), cancellationToken);

    private void Cleanup(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var deleted = 0;
        var failed = 0;
        foreach (var directory in new[] { "app", "error", "audit" })
        {
            DeleteLogs(Path.Combine(logRoot, directory), now.Date.AddDays(-Math.Max(logDays, 1)),
                ref deleted, ref failed, cancellationToken);
        }
        DeleteOldFiles(exportRoot, now.AddHours(-Math.Max(exportHours, 1)),
            ref deleted, ref failed, cancellationToken);
        DeleteOldFiles(tmpRoot, now.AddHours(-Math.Max(tmpHours, 1)),
            ref deleted, ref failed, cancellationToken);
        logger.LogInformation("数据清理完成：删除 {Deleted} 个文件，失败 {Failed} 个。", deleted, failed);
    }

    private void DeleteLogs(string directory, DateTime cutoff, ref int deleted, ref int failed,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;
        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(directory).EnumerateFiles().ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed++;
            logger.LogWarning(ex, "无法枚举日志目录 {Directory}", directory);
            return;
        }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = file.Name;
            var separator = name.IndexOf('-');
            if (separator < 0) continue;
            var dateText = name.Length >= separator + 11 ? name.Substring(separator + 1, 10) : "";
            if (!DateTime.TryParseExact(dateText, "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var fileDate) || fileDate >= cutoff)
                continue;
            TryDelete(file, ref deleted, ref failed);
        }
    }

    private void DeleteOldFiles(string directory, DateTime cutoff, ref int deleted, ref int failed,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;
        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed++;
            logger.LogWarning(ex, "无法枚举临时目录 {Directory}", directory);
            return;
        }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc < cutoff) TryDelete(file, ref deleted, ref failed);
        }
    }

    private void TryDelete(FileInfo file, ref int deleted, ref int failed)
    {
        try { file.Delete(); deleted++; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed++;
            logger.LogWarning(ex, "清理文件失败 {Path}", file.FullName);
        }
    }
}
