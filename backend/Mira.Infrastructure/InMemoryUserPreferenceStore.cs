using Mira.Core;

namespace Mira.Infrastructure;

// Process-memory IUserPreferenceStore: a test double with the same semantics as SqliteUserPreferenceStore.
public sealed class InMemoryUserPreferenceStore : IUserPreferenceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Dictionary<string, string>> _rows = new();

    public Task<UserPreferences> GetAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            return Task.FromResult(Read(ownerUserId));
        }
    }

    public Task<UserPreferences> UpdateAsync(Guid ownerUserId, PreferenceUpdate update, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        ArgumentNullException.ThrowIfNull(update);
        if (update.IsEmpty) throw new ArgumentException("At least one preference is required.", nameof(update));

        lock (_gate)
        {
            if (!_rows.TryGetValue(ownerUserId, out var rows)) _rows[ownerUserId] = rows = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in update.ToStored()) rows[key] = value;
            return Task.FromResult(Read(ownerUserId));
        }
    }

    // Callers hold _gate.
    private UserPreferences Read(Guid ownerUserId) =>
        _rows.TryGetValue(ownerUserId, out var rows) ? UserPreferences.FromStored(rows) : UserPreferences.Default;

    private static void RequireOwner(Guid ownerUserId)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("An authenticated owner is required.", nameof(ownerUserId));
    }
}
