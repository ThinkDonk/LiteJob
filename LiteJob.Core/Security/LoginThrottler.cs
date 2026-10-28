using System.Collections.Concurrent;

namespace LiteJob.Core.Security;

public sealed class LoginThrottler
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Fails, DateTime LockUntil)> _entries = new();

    public bool IsLocked(string username)
        => _entries.TryGetValue(username, out var entry) && entry.LockUntil > DateTime.UtcNow;

    public void RecordFailure(string username)
    {
        _entries.AddOrUpdate(username,
            _ => (1, DateTime.MinValue),
            (_, entry) =>
            {
                if (entry.LockUntil > DateTime.UtcNow)
                    return entry;
                if (entry.LockUntil != DateTime.MinValue && entry.LockUntil <= DateTime.UtcNow)
                    return (1, DateTime.MinValue);
                var fails = entry.Fails + 1;
                return fails >= MaxFailures
                    ? (fails, DateTime.UtcNow.Add(LockDuration))
                    : (fails, DateTime.MinValue);
            });
    }

    public void RecordSuccess(string username)
    {
        _entries.TryRemove(username, out _);
    }
}
