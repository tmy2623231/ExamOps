using System.Security.Cryptography;
using ExamOps.Model.Common;

namespace ExamOps.Infra.Storage;

public sealed record StoredImportFile(string RelativePath, string Sha256, long SizeBytes);
public sealed record StoredExportFile(string RelativePath, string Sha256, long SizeBytes);

public interface IFileStorage
{
    bool TryProbeWritable();
    Task<StoredImportFile> SaveImportAsync(Stream content, string originalName, CancellationToken cancellationToken = default);
    Task<FileStream> OpenImportAsync(string relativePath, CancellationToken cancellationToken = default);
    void DeleteImport(string relativePath);
    Task<StoredExportFile> SaveExportAsync(byte[] content, string extension, CancellationToken cancellationToken = default);
    Task<FileStream> OpenExportAsync(string relativePath, CancellationToken cancellationToken = default);
    void DeleteExport(string relativePath);
}

public sealed class FileStorage(string uploadRoot, string tmpRoot, string exportRoot, IClock clock) : IFileStorage
{
    private const long MaxImportBytes = 10 * 1024 * 1024;
    private const long MaxExportBytes = 25 * 1024 * 1024;

    public bool TryProbeWritable()
    {
        try
        {
            Directory.CreateDirectory(uploadRoot);
            Directory.CreateDirectory(tmpRoot);
            Directory.CreateDirectory(exportRoot);
            foreach (var root in new[] { uploadRoot, tmpRoot, exportRoot })
            {
                var probe = Path.Combine(root, $".write-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<StoredImportFile> SaveImportAsync(Stream content, string originalName, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(originalName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只支持 .xlsx 文件。");
        var root = Path.GetFullPath(uploadRoot);
        var destination = Path.GetFullPath(Path.Combine(root, "question-import", clock.UtcNow.ToString("yyyy/MM"), $"{Guid.NewGuid():N}.xlsx"));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("非法存储路径。");
        var temporary = Path.Combine(Path.GetFullPath(tmpRoot), $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(tmpRoot);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (output.Length + read > MaxImportBytes) throw new InvalidDataException("文件不能超过 10 MiB。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (output.Length == 0) throw new InvalidDataException("文件不能为空。");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Windows 不允许移动仍由 File.Create 独占打开的临时文件，必须先释放写入句柄。
            File.Move(temporary, destination);
            return new StoredImportFile(Path.GetRelativePath(root, destination).Replace('\\', '/'),
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), new FileInfo(destination).Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<FileStream> OpenImportAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(uploadRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var moduleRoot = Path.GetFullPath(Path.Combine(root, "question-import"));
        if (Path.IsPathRooted(relativePath) || !path.StartsWith(moduleRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("非法文件路径。");
        return Task.FromResult(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));
    }

    public void DeleteImport(string relativePath)
    {
        var root = Path.GetFullPath(uploadRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var moduleRoot = Path.GetFullPath(Path.Combine(root, "question-import"));
        if (!Path.IsPathRooted(relativePath) && path.StartsWith(moduleRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            File.Delete(path);
    }

    public async Task<StoredExportFile> SaveExportAsync(byte[] content, string extension,
        CancellationToken cancellationToken = default)
    {
        var normalized = extension.ToLowerInvariant();
        if (normalized is not (".csv" or ".xlsx")) throw new InvalidDataException("不支持的导出格式。");
        if (content.Length == 0 || content.Length > MaxExportBytes) throw new InvalidDataException("导出文件为空或超过 25 MiB。");
        var root = Path.GetFullPath(exportRoot);
        var destination = Path.GetFullPath(Path.Combine(root, "reports", clock.UtcNow.ToString("yyyy/MM"), $"{Guid.NewGuid():N}{normalized}"));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("非法存储路径。");
        var temporary = Path.Combine(Path.GetFullPath(tmpRoot), $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(tmpRoot);
            await using (var output = File.Create(temporary))
                await output.WriteAsync(content, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(temporary, destination);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(content);
            return new StoredExportFile(Path.GetRelativePath(root, destination).Replace('\\', '/'),
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), content.LongLength);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<FileStream> OpenExportAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(exportRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var moduleRoot = Path.GetFullPath(Path.Combine(root, "reports"));
        if (Path.IsPathRooted(relativePath) || !path.StartsWith(moduleRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("非法文件路径。");
        return Task.FromResult(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));
    }

    public void DeleteExport(string relativePath)
    {
        var root = Path.GetFullPath(exportRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var moduleRoot = Path.GetFullPath(Path.Combine(root, "reports"));
        if (!Path.IsPathRooted(relativePath) && path.StartsWith(moduleRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            File.Delete(path);
    }
}
