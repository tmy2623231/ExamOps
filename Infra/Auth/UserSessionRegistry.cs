using System.Collections.Concurrent;
using ExamOps.BLL;

namespace ExamOps.Infra.Auth;

/// <summary>
/// P0-1: Tracks active Blazor Server circuits by user ID so disabling an account
/// or changing its permissions/password immediately disconnects open sessions.
/// </summary>
public sealed class UserSessionRegistry(ILogger<UserSessionRegistry> logger) : IUserSessionInvalidator
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, Func<Task>>> _sessions = new();

    public IDisposable Register(long userId, Func<Task> onRevoked)
    {
        var circuitKey = Guid.NewGuid().ToString("N");
        var bucket = _sessions.GetOrAdd(userId, _ => new ConcurrentDictionary<string, Func<Task>>());
        bucket[circuitKey] = onRevoked;
        return new Subscription(this, userId, circuitKey);
    }

    public int GetActiveSessionCount(long userId)
        => _sessions.TryGetValue(userId, out var bucket) ? bucket.Count : 0;

    public async Task InvalidateUserAsync(long userId)
    {
        if (!_sessions.TryGetValue(userId, out var bucket) || bucket.IsEmpty)
            return;

        var callbacks = bucket.Values.ToArray();
        logger.LogInformation("正在向用户 {UserId} 的 {Count} 个在线会话广播下线通知", userId, callbacks.Length);
        foreach (var cb in callbacks)
        {
            try
            {
                await cb();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "向用户 {UserId} 广播会话下线时单个连接已断开", userId);
            }
        }
    }

    private void Unregister(long userId, string circuitKey)
    {
        if (_sessions.TryGetValue(userId, out var bucket))
        {
            bucket.TryRemove(circuitKey, out _);
            if (bucket.IsEmpty)
                _sessions.TryRemove(userId, out _);
        }
    }

    private sealed class Subscription(UserSessionRegistry registry, long userId, string circuitKey) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.Unregister(userId, circuitKey);
        }
    }
}
