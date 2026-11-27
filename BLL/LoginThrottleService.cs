using ExamOps.Model.Common;

namespace ExamOps.BLL;

public sealed class LoginThrottleService(IClock clock)
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTime>> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBlocked(string username, string ip)
    {
        lock (_gate)
        {
            Cleanup();
            return GetQueue(username, ip).Count >= MaxAttempts;
        }
    }

    public void RecordFailure(string username, string ip)
    {
        lock (_gate)
        {
            Cleanup();
            GetQueue(username, ip).Enqueue(clock.UtcNow);
        }
    }

    public void Reset(string username, string ip)
    {
        lock (_gate) { _attempts.Remove(Key(username, ip)); }
    }

    private Queue<DateTime> GetQueue(string username, string ip)
    {
        var key = Key(username, ip);
        if (!_attempts.TryGetValue(key, out var queue))
        {
            queue = new Queue<DateTime>();
            _attempts[key] = queue;
        }
        return queue;
    }

    private void Cleanup()
    {
        var cutoff = clock.UtcNow - Window;
        foreach (var key in _attempts.Keys.ToArray())
        {
            var queue = _attempts[key];
            while (queue.TryPeek(out var at) && at < cutoff) queue.Dequeue();
            if (queue.Count == 0) _attempts.Remove(key);
        }
    }

    private static string Key(string username, string ip) => $"{username.Trim().ToLowerInvariant()}|{ip}";
}
