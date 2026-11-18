using Microsoft.Data.Sqlite;
using Mira.Core;

namespace Mira.Infrastructure.Sqlite;

// SQLite IPersonaStore (M4). Every query is owner-scoped: writes filter OwnerUserId = $owner only (never OR OwnerUserId IS NULL);
// reads may also match the built-in (OwnerUserId IS NULL). Version queries always join Personas for the owner filter.
// Create, update, revert, default change, conversation-persona change and delete each run in one BEGIN IMMEDIATE transaction
// (Microsoft.Data.Sqlite's non-deferred BeginTransaction). Cap and Version = MAX+1 are decided inside that transaction.
public sealed class SqlitePersonaStore(MiraDatabase database, TimeProvider time) : IPersonaStore
{
    private const int SqliteConstraint = 19; // SQLITE_CONSTRAINT; extended 1555 = PRIMARYKEY, 2067 = UNIQUE

    private const string ReadableScope = "(p.OwnerUserId IS NULL OR p.OwnerUserId = $owner)";

    private const string DetailColumns = """
        p.Id, p.OwnerUserId, p.ActiveVersion, p.CreatedAt, p.UpdatedAt, v.Name, v.Description, v.Markdown
        FROM Personas p JOIN PersonaVersions v ON v.PersonaId = p.Id AND v.Version = p.ActiveVersion
        """;

    public async Task<IReadOnlyList<Persona>> ListAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {DetailColumns}
            WHERE {ReadableScope}
            ORDER BY p.OwnerUserId IS NOT NULL, p.CreatedAt, p.Id;
            """;
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        var list = new List<Persona>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) list.Add(InMemoryPersonaStore.Summary(ReadDetail(reader)));
        return list;
    }

    public async Task<PersonaDetail?> GetAsync(Guid ownerUserId, Guid personaId, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        return await ReadReadableAsync(connection, null, ownerUserId, personaId, cancellationToken);
    }

    public async Task<IReadOnlyList<PersonaVersionInfo>> ListVersionsAsync(Guid ownerUserId, Guid personaId, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.PersonaId, v.Version, v.Name, v.Description, v.CreatedAt
            FROM PersonaVersions v JOIN Personas p ON p.Id = v.PersonaId
            WHERE v.PersonaId = $id AND {ReadableScope}
            ORDER BY v.Version DESC
            LIMIT $take;
            """;
        AddScope(command, ownerUserId, personaId);
        command.Parameters.AddWithValue("$take", PersonaLimits.MaxVersionsPerPersona);
        var versions = new List<PersonaVersionInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(new PersonaVersionInfo(
                Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), MiraDatabase.ParseTime(reader.GetString(4))));
        }

        return versions;
    }

    public async Task<PersonaVersion?> GetVersionAsync(Guid ownerUserId, Guid personaId, int version, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.PersonaId, v.Version, v.Name, v.Description, v.Markdown, v.CreatedAt
            FROM PersonaVersions v JOIN Personas p ON p.Id = v.PersonaId
            WHERE v.PersonaId = $id AND v.Version = $version AND {ReadableScope};
            """;
        AddScope(command, ownerUserId, personaId);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PersonaVersion(
            Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), MiraDatabase.ParseTime(reader.GetString(5)));
    }

    public async Task<PersonaDetail> CreateAsync(Guid ownerUserId, string name, string? description, string markdown, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        var (n, d, m) = InMemoryPersonaStore.Clean(name, description, markdown);
        return await WriteAsync(async (connection, transaction) =>
        {
            await using (var count = Command(connection, transaction, "SELECT COUNT(*) FROM Personas WHERE OwnerUserId = $owner;"))
            {
                count.Parameters.AddWithValue("$owner", ownerUserId.ToString());
                if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) >= PersonaLimits.MaxUserPersonas)
                {
                    throw new PersonaConflictException("Persona limit reached.");
                }
            }

            var id = Guid.NewGuid();
            var now = MiraDatabase.FormatTime(time.GetUtcNow());
            await using (var insert = Command(connection, transaction, """
                INSERT INTO Personas (Id, OwnerUserId, ActiveVersion, CreatedAt, UpdatedAt) VALUES ($id, $owner, 1, $now, $now);
                """))
            {
                AddScope(insert, ownerUserId, id);
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertVersionAsync(connection, transaction, id, 1, n, d, m, now, cancellationToken);
            return (await ReadOwnedAsync(connection, transaction, ownerUserId, id, cancellationToken))!;
        }, cancellationToken);
    }

    public async Task<PersonaDetail> UpdateAsync(Guid ownerUserId, Guid personaId, string name, string? description, string markdown, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        var (n, d, m) = InMemoryPersonaStore.Clean(name, description, markdown);
        return await WriteAsync(async (connection, transaction) =>
        {
            int next;
            await using (var max = Command(connection, transaction, """
                SELECT MAX(v.Version) FROM PersonaVersions v JOIN Personas p ON p.Id = v.PersonaId
                WHERE p.Id = $id AND p.OwnerUserId = $owner;
                """))
            {
                AddScope(max, ownerUserId, personaId);
                var value = await max.ExecuteScalarAsync(cancellationToken);
                if (value is null or DBNull) throw new KeyNotFoundException("Persona not found for this owner.");
                next = Convert.ToInt32(value) + 1;
            }

            var now = MiraDatabase.FormatTime(time.GetUtcNow());
            await InsertVersionAsync(connection, transaction, personaId, next, n, d, m, now, cancellationToken);
            await RetargetAsync(connection, transaction, ownerUserId, personaId, next, now, cancellationToken);

            // Keep the newest MaxVersionsPerPersona versions (the new, active one is the newest).
            await using (var prune = Command(connection, transaction, """
                DELETE FROM PersonaVersions
                WHERE PersonaId = (SELECT Id FROM Personas WHERE Id = $id AND OwnerUserId = $owner)
                  AND Version NOT IN (SELECT Version FROM PersonaVersions WHERE PersonaId = $id ORDER BY Version DESC LIMIT $keep);
                """))
            {
                AddScope(prune, ownerUserId, personaId);
                prune.Parameters.AddWithValue("$keep", PersonaLimits.MaxVersionsPerPersona);
                await prune.ExecuteNonQueryAsync(cancellationToken);
            }

            return (await ReadOwnedAsync(connection, transaction, ownerUserId, personaId, cancellationToken))!;
        }, cancellationToken);
    }

    public async Task<PersonaDetail> RevertAsync(Guid ownerUserId, Guid personaId, int version, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        return await WriteAsync(async (connection, transaction) =>
        {
            await using (var exists = Command(connection, transaction, """
                SELECT 1 FROM PersonaVersions v JOIN Personas p ON p.Id = v.PersonaId
                WHERE p.Id = $id AND p.OwnerUserId = $owner AND v.Version = $version;
                """))
            {
                AddScope(exists, ownerUserId, personaId);
                exists.Parameters.AddWithValue("$version", version);
                if (await exists.ExecuteScalarAsync(cancellationToken) is null) throw new KeyNotFoundException("Persona version not found for this owner.");
            }

            await RetargetAsync(connection, transaction, ownerUserId, personaId, version, MiraDatabase.FormatTime(time.GetUtcNow()), cancellationToken);
            return (await ReadOwnedAsync(connection, transaction, ownerUserId, personaId, cancellationToken))!;
        }, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid ownerUserId, Guid personaId, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (!await OwnsAsync(connection, transaction, ownerUserId, personaId, cancellationToken)) return false;

            // Reset a default that points here; conversation pins are deliberately left in place (soft pin).
            await using (var reset = Command(connection, transaction, """
                UPDATE UserPersonaSettings SET DefaultPersonaId = $builtIn, UpdatedAt = $now
                WHERE OwnerUserId = $owner AND DefaultPersonaId = $id;
                """))
            {
                AddScope(reset, ownerUserId, personaId);
                reset.Parameters.AddWithValue("$builtIn", PersonaIds.BuiltInMira.ToString());
                reset.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(time.GetUtcNow()));
                await reset.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var delete = Command(connection, transaction, "DELETE FROM Personas WHERE Id = $id AND OwnerUserId = $owner;");
            AddScope(delete, ownerUserId, personaId);
            return await delete.ExecuteNonQueryAsync(cancellationToken) == 1; // versions cascade
        }, cancellationToken);
    }

    public async Task<Guid> GetDefaultAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT DefaultPersonaId FROM UserPersonaSettings WHERE OwnerUserId = $owner;");
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is string id ? Guid.Parse(id) : PersonaIds.BuiltInMira;
    }

    public async Task SetDefaultAsync(Guid ownerUserId, Guid personaId, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        await WriteAsync(async (connection, transaction) =>
        {
            if (!await ReadableAsync(connection, transaction, ownerUserId, personaId, cancellationToken))
            {
                throw new KeyNotFoundException("Persona not found for this owner.");
            }

            await using var upsert = Command(connection, transaction, """
                INSERT INTO UserPersonaSettings (OwnerUserId, DefaultPersonaId, UpdatedAt) VALUES ($owner, $id, $now)
                ON CONFLICT (OwnerUserId) DO UPDATE SET DefaultPersonaId = excluded.DefaultPersonaId, UpdatedAt = excluded.UpdatedAt;
                """);
            AddScope(upsert, ownerUserId, personaId);
            upsert.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(time.GetUtcNow()));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task<PersonaResolution> ResolveForTurnAsync(Guid ownerUserId, Guid pinnedPersonaId, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        if (await ReadReadableAsync(connection, null, ownerUserId, pinnedPersonaId, cancellationToken) is { } pinned)
        {
            return new PersonaResolution(pinned, false);
        }

        // Gone (or never readable by this owner): prompt with the built-in only. The stored pin is not rewritten.
        var builtIn = await ReadReadableAsync(connection, null, ownerUserId, PersonaIds.BuiltInMira, cancellationToken) ?? BuiltInPersona.Detail;
        return new PersonaResolution(builtIn, true);
    }

    // PUT /api/conversations/{id}/persona: in one BEGIN IMMEDIATE transaction the conversation must be owned and the persona
    // must be built-in or owned; then the soft pin is written. Not on IPersonaStore (it spans the conversation table).
    public async Task<ConversationPersonaResult> SetConversationPersonaAsync(Guid ownerUserId, Guid conversationId, Guid personaId, CancellationToken cancellationToken = default)
    {
        InMemoryPersonaStore.RequireOwner(ownerUserId);
        return await WriteAsync(async (connection, transaction) =>
        {
            await using (var conversation = Command(connection, transaction, "SELECT 1 FROM Conversations WHERE Id = $conversationId AND OwnerUserId = $owner;"))
            {
                conversation.Parameters.AddWithValue("$conversationId", conversationId.ToString());
                conversation.Parameters.AddWithValue("$owner", ownerUserId.ToString());
                if (await conversation.ExecuteScalarAsync(cancellationToken) is null) return ConversationPersonaResult.ConversationNotFound;
            }

            if (!await ReadableAsync(connection, transaction, ownerUserId, personaId, cancellationToken)) return ConversationPersonaResult.PersonaNotFound;

            await using var update = Command(connection, transaction, """
                UPDATE Conversations SET PersonaId = $id, UpdatedAt = $now WHERE Id = $conversationId AND OwnerUserId = $owner;
                """);
            AddScope(update, ownerUserId, personaId);
            update.Parameters.AddWithValue("$conversationId", conversationId.ToString());
            update.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(time.GetUtcNow()));
            await update.ExecuteNonQueryAsync(cancellationToken);
            return ConversationPersonaResult.Updated;
        }, cancellationToken);
    }

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        try
        {
            // BEGIN IMMEDIATE: take the write lock up front so count/MAX+1 checks and the write cannot interleave.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var result = await work(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint && ex.SqliteExtendedErrorCode is 1555 or 2067)
        {
            // Primary-key/unique clash (for example two saves racing for the same version number): fixed 409, never a 500.
            throw new PersonaConflictException("The persona changed at the same time. Try again.");
        }
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid personaId, int version, string name, string? description, string markdown,
        string now, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO PersonaVersions (PersonaId, Version, Name, Description, Markdown, CreatedAt)
            VALUES ($id, $version, $name, $description, $markdown, $now);
            """);
        command.Parameters.AddWithValue("$id", personaId.ToString());
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("$markdown", markdown);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RetargetAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid ownerUserId, Guid personaId, int version, string now, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "UPDATE Personas SET ActiveVersion = $version, UpdatedAt = $now WHERE Id = $id AND OwnerUserId = $owner;");
        AddScope(command, ownerUserId, personaId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$now", now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new KeyNotFoundException("Persona not found for this owner.");
    }

    private static async Task<bool> OwnsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid ownerUserId, Guid personaId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT 1 FROM Personas WHERE Id = $id AND OwnerUserId = $owner;");
        AddScope(command, ownerUserId, personaId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> ReadableAsync(SqliteConnection connection, SqliteTransaction transaction, Guid ownerUserId, Guid personaId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT 1 FROM Personas p WHERE p.Id = $id AND {ReadableScope};");
        AddScope(command, ownerUserId, personaId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<PersonaDetail?> ReadReadableAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid ownerUserId, Guid personaId, CancellationToken cancellationToken) =>
        await ReadDetailAsync(connection, transaction, ownerUserId, personaId, ReadableScope, cancellationToken);

    private static async Task<PersonaDetail?> ReadOwnedAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid ownerUserId, Guid personaId, CancellationToken cancellationToken) =>
        await ReadDetailAsync(connection, transaction, ownerUserId, personaId, "p.OwnerUserId = $owner", cancellationToken);

    private static async Task<PersonaDetail?> ReadDetailAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid ownerUserId, Guid personaId, string scope, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {DetailColumns} WHERE p.Id = $id AND {scope};");
        AddScope(command, ownerUserId, personaId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDetail(reader) : null;
    }

    private static PersonaDetail ReadDetail(SqliteDataReader reader)
    {
        var owner = reader.IsDBNull(1) ? (Guid?)null : Guid.Parse(reader.GetString(1));
        return new PersonaDetail(
            Guid.Parse(reader.GetString(0)), owner, reader.GetInt32(2), owner is null,
            MiraDatabase.ParseTime(reader.GetString(3)), MiraDatabase.ParseTime(reader.GetString(4)),
            reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7));
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddScope(SqliteCommand command, Guid ownerUserId, Guid personaId)
    {
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        command.Parameters.AddWithValue("$id", personaId.ToString());
    }
}
