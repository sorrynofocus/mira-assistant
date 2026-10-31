using System.Collections.Concurrent;
using Mira.Core;

namespace Mira.Api.Auth;

// Per-username failure counter. Applies to unknown usernames too, so throttling does not reveal which accounts exist.
public sealed class LoginThrottle(MiraOptions options, TimeProvider time)
{
    private const int PruneThreshold = 10_000;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private TimeSpan Window => TimeSpan.FromMinutes(options.Auth.FailedLoginWindowMinutes);

    public bool IsLockedOut(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return false;
        if (time.GetUtcNow() - entry.WindowStart >= Window)
        {
            _entries.TryRemove(KeyValuePair.Create(key, entry));
            return false;
        }

        return entry.Failures >= options.Auth.MaxFailedLoginsPerUsername;
    }

    public void RecordFailure(string key)
    {
        var now = time.GetUtcNow();
        _entries.AddOrUpdate(
            key,
            _ => new Entry(1, now),
            (_, entry) => now - entry.WindowStart >= Window ? new Entry(1, now) : entry with { Failures = entry.Failures + 1 });

        if (_entries.Count > PruneThreshold) Prune(now);
    }

    public void Reset(string key) => _entries.TryRemove(key, out _);

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            if (now - pair.Value.WindowStart >= Window) _entries.TryRemove(pair);
        }
    }

    private sealed record Entry(int Failures, DateTimeOffset WindowStart);
}
