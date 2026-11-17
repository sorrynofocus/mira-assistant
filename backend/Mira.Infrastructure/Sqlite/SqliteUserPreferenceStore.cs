using Microsoft.Data.Sqlite;
using Mira.Core;

namespace Mira.Infrastructure.Sqlite;

// SQLite IUserPreferenceStore (M3). Owner-scoped key/value rows; parameterized SQL only. Only rows produced by
// PreferenceUpdate.ToStored (allowlisted keys and values) are written, and reads fall back to defaults via UserPreferences.FromStored.
public sealed class SqliteUserPreferenceStore(MiraDatabase database, TimeProvider time) : IUserPreferenceStore
{
    public async Task<UserPreferences> GetAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        return await ReadAsync(connection, null, ownerUserId, cancellationToken);
    }

    public async Task<UserPreferences> UpdateAsync(Guid ownerUserId, PreferenceUpdate update, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        ArgumentNullException.ThrowIfNull(update);
        if (update.IsEmpty) throw new ArgumentException("At least one preference is required.", nameof(update));

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var now = MiraDatabase.FormatTime(time.GetUtcNow());
        foreach (var (key, value) in update.ToStored())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO UserPreferences (OwnerUserId, Key, Value, UpdatedAt)
                VALUES ($owner, $key, $value, $now)
                ON CONFLICT (OwnerUserId, Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt;
                """;
            command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = await ReadAsync(connection, transaction, ownerUserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<UserPreferences> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid ownerUserId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Key, Value FROM UserPreferences WHERE OwnerUserId = $owner;";
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows[reader.GetString(0)] = reader.GetString(1);
        return UserPreferences.FromStored(rows);
    }

    private static void RequireOwner(Guid ownerUserId)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("An authenticated owner is required.", nameof(ownerUserId));
    }
}
