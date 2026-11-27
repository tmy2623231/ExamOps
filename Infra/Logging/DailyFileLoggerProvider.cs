using System.Collections.Concurrent;
using System.Text;
using ExamOps.Model.Common;
using Microsoft.Extensions.Logging;

namespace ExamOps.Infra.Logging;

public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 50 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, RollingWriter> Writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly IClock _clock;

    public DailyFileLoggerProvider(string root, IClock clock)
    {
        _root = Path.GetFullPath(root);
        _clock = clock;
    }

    public ILogger CreateLogger(string categoryName)
        => new DailyFileLogger(this, categoryName);

    public void Dispose()
    {
        foreach (var key in Writers.Keys.Where(x => x.StartsWith(_root, StringComparison.OrdinalIgnoreCase)))
            if (Writers.TryRemove(key, out var writer)) writer.Dispose();
    }

    private void Write(string directory, string prefix, string line)
    {
        var writer = Writers.GetOrAdd(Path.Combine(_root, directory), path => new RollingWriter(path, prefix, _clock));
        writer.WriteLine(line, MaxFileBytes);
    }

    private sealed class DailyFileLogger(DailyFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var timestamp = provider._clock.UtcNow.ToString("O");
            var message = $"{timestamp} [{logLevel}] {category} ({eventId.Id}) {formatter(state, exception)}";
            if (exception is not null) message += Environment.NewLine + exception;
            if (category == "Audit") provider.Write("audit", "audit", message);
            else
            {
                provider.Write("app", "app", message);
                if (logLevel >= LogLevel.Warning) provider.Write("error", "error", message);
            }
        }
    }

    private sealed class RollingWriter(string directory, string prefix, IClock clock) : IDisposable
    {
        private readonly object _gate = new();
        private StreamWriter? _writer;
        private FileStream? _stream;
        private string? _date;
        private int _sequence;

        public void WriteLine(string line, long maxBytes)
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var bytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                    EnsureOpen(bytes, maxBytes);
                    _writer!.WriteLine(line);
                    _writer.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 日志写盘故障（如磁盘满/权限异常）绝不能向业务调用方传播，避免已提交事务被误判失败。
                    Close();
                }
            }
        }

        private void EnsureOpen(int incomingBytes, long maxBytes)
        {
            var date = clock.UtcNow.ToString("yyyy-MM-dd");
            if (_stream is not null && _writer is not null && _date == date && _stream.Length + incomingBytes <= maxBytes)
                return;
            Close();
            _date = date;
            _sequence = 1;
            while (true)
            {
                var suffix = _sequence == 1 ? "" : $"-{_sequence}";
                var path = Path.Combine(directory, $"{prefix}-{date}{suffix}.log");
                if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= maxBytes)
                {
                    _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = false };
                    return;
                }
                _sequence++;
            }
        }

        private void Close()
        {
            _writer?.Dispose();
            _stream?.Dispose();
            _writer = null;
            _stream = null;
        }

        public void Dispose()
        {
            lock (_gate) Close();
        }
    }
}
