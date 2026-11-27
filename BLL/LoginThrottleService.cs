using ExamOps.Model.Common;

namespace ExamOps.BLL;

public sealed class LoginThrottleService(IClock clock)
{
    private const int MaxAttemptsPerIp = 5;
    private const int MaxAttemptsPerAccount = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTime>> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBlocked(string username, string ip)
    {
        lock (_gate)
        {
            Cleanup();
            return GetQueue(IpKey(username, ip)).Count >= MaxAttemptsPerIp
                || GetQueue(AccountKey(username)).Count >= MaxAttemptsPerAccount;
        }
    }

    public void RecordFailure(string username, string ip)
    {
        lock (_gate)
        {
            Cleanup();
            var now = clock.UtcNow;
            GetQueue(IpKey(username, ip)).Enqueue(now);
            GetQueue(AccountKey(username)).Enqueue(now);
        }
    }

    public void Reset(string username, string ip)
    {
        lock (_gate)
        {
            _attempts.Remove(IpKey(username, ip));
            _attempts.Remove(AccountKey(username));
        }
    }

    private Queue<DateTime> GetQueue(string key)
    {
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

    private static string IpKey(string username, string ip) => $"ip|{username.Trim().ToLowerInvariant()}|{ip}";
    private static string AccountKey(string username) => $"acct|{username.Trim().ToLowerInvariant()}";
}
