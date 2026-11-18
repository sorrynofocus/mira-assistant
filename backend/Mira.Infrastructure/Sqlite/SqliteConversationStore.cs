using Microsoft.Data.Sqlite;
using Mira.Core;

namespace Mira.Infrastructure.Sqlite;

// SQLite IConversationStore (M2). Every statement filters on Conversations.OwnerUserId; message statements reach their rows
// only through an owned conversation. Parameterized SQL only. Message order is CreatedAt, then insertion order (rowid).
public sealed class SqliteConversationStore(MiraDatabase database, TimeProvider time) : IConversationStore
{
    private const string OwnedConversation = "Id = $conversationId AND OwnerUserId = $owner";

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, ConversationLimits.MaxPageSize);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, CreatedAt, UpdatedAt FROM Conversations
            WHERE OwnerUserId = $owner
            ORDER BY UpdatedAt DESC, Id
            LIMIT $take OFFSET $skip;
            """;
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);
        var page = new List<ConversationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) page.Add(ReadSummary(reader));
        return page;
    }

    public Task<ConversationSummary> CreateAsync(Guid ownerUserId, string? title, CancellationToken cancellationToken = default) =>
        CreateAsync(ownerUserId, title, PersonaIds.BuiltInMira, cancellationToken);

    public async Task<ConversationSummary> CreateAsync(Guid ownerUserId, string? title, Guid personaId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        RequirePersona(personaId);
        var normalized = ConversationLimits.NormalizeTitle(title);
        await using var connection = await database.OpenAsync(cancellationToken);
        // Migration 5 (chunk A) persists personaId. Until then the returned pin is the built-in MIRA.
        return await InsertConversationAsync(connection, null, ownerUserId, normalized, cancellationToken);
    }

    public async Task<ConversationSnapshot?> GetAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, Title, CreatedAt, UpdatedAt FROM Conversations WHERE {OwnedConversation};";
        AddScope(command, ownerUserId, conversationId);
        ConversationSummary summary;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            summary = ReadSummary(reader);
        }

        return await SnapshotAsync(connection, null, ownerUserId, summary, cancellationToken);
    }

    public Task<ConversationSnapshot> GetMostRecentOrCreateAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
        GetMostRecentOrCreateAsync(ownerUserId, PersonaIds.BuiltInMira, cancellationToken);

    public async Task<ConversationSnapshot> GetMostRecentOrCreateAsync(Guid ownerUserId, Guid personaIdForNew, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        RequirePersona(personaIdForNew);
        // personaIdForNew is accepted so the interface compiles; migration 5 persists it. New rows currently report BuiltInMira.
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        ConversationSummary? summary = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT Id, Title, CreatedAt, UpdatedAt FROM Conversations
                WHERE OwnerUserId = $owner
                ORDER BY UpdatedAt DESC, Id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) summary = ReadSummary(reader);
        }

        summary ??= await InsertConversationAsync(connection, transaction, ownerUserId, null, cancellationToken);
        var snapshot = await SnapshotAsync(connection, transaction, ownerUserId, summary, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<bool> RenameAsync(Guid ownerUserId, Guid conversationId, string? title, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        var normalized = ConversationLimits.NormalizeTitle(title);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE Conversations SET Title = $title, UpdatedAt = $now WHERE {OwnedConversation};";
        AddScope(command, ownerUserId, conversationId);
        command.Parameters.AddWithValue("$title", (object?)normalized ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(time.GetUtcNow()));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> DeleteAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsAsync(connection, transaction, ownerUserId, conversationId, cancellationToken)) return false;

        string[] statements =
        [
            "DELETE FROM PipelineEvents WHERE ConversationId = $conversationId AND OwnerUserId = $owner;",
            $"DELETE FROM Messages WHERE ConversationId IN (SELECT Id FROM Conversations WHERE {OwnedConversation});",
            $"DELETE FROM Conversations WHERE {OwnedConversation};"
        ];
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            AddScope(command, ownerUserId, conversationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<ConversationMessage> AppendUserMessageAsync(Guid ownerUserId, Guid conversationId, string content, string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return AppendAsync(ownerUserId, conversationId, ChatRole.User, content, MessageStatus.Completed, null, requestId, cancellationToken);
    }

    public Task<ConversationMessage> BeginAssistantMessageAsync(Guid ownerUserId, Guid conversationId, string modelId, string requestId, CancellationToken cancellationToken = default) =>
        AppendAsync(ownerUserId, conversationId, ChatRole.Assistant, string.Empty, MessageStatus.Streaming, modelId, requestId, cancellationToken);

    public async Task FinishAssistantMessageAsync(Guid ownerUserId, Guid conversationId, Guid messageId, string content, MessageStatus status, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        ArgumentNullException.ThrowIfNull(content);
        if (status == MessageStatus.Streaming) throw new ArgumentException("A finished message cannot remain streaming.", nameof(status));

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE Messages SET Content = $content, Status = $status
                WHERE Id = $messageId AND Role = $assistant AND ConversationId IN (SELECT Id FROM Conversations WHERE {OwnedConversation});
                """;
            AddScope(command, ownerUserId, conversationId);
            command.Parameters.AddWithValue("$messageId", messageId.ToString());
            command.Parameters.AddWithValue("$assistant", nameof(ChatRole.Assistant));
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$status", status.ToString());
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new KeyNotFoundException("Assistant message not found for this owner.");
            }
        }

        await TouchAsync(connection, transaction, ownerUserId, conversationId, time.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatTurn>> GetPromptTurnsAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        await using var connection = await database.OpenAsync(cancellationToken);
        var messages = await ReadMessagesAsync(connection, null, ownerUserId, conversationId, cancellationToken);
        return messages
            .Where(m => m.Role == ChatRole.User || m.Status == MessageStatus.Completed)
            .Select(m => new ChatTurn(m.Role, m.Content))
            .ToArray();
    }

    private async Task<ConversationMessage> AppendAsync(
        Guid ownerUserId, Guid conversationId, ChatRole role, string content, MessageStatus status, string? modelId, string requestId,
        CancellationToken cancellationToken)
    {
        RequireOwner(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsAsync(connection, transaction, ownerUserId, conversationId, cancellationToken))
        {
            throw new KeyNotFoundException("Conversation not found for this owner.");
        }

        var now = time.GetUtcNow();
        var message = new ConversationMessage(Guid.NewGuid(), role, content, status, now, modelId, requestId);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Messages (Id, ConversationId, Role, Content, Status, CreatedAt, ModelId, RequestId)
                VALUES ($id, $conversationId, $role, $content, $status, $createdAt, $modelId, $requestId);
                """;
            command.Parameters.AddWithValue("$id", message.Id.ToString());
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString());
            command.Parameters.AddWithValue("$role", role.ToString());
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$createdAt", MiraDatabase.FormatTime(now));
            command.Parameters.AddWithValue("$modelId", (object?)modelId ?? DBNull.Value);
            command.Parameters.AddWithValue("$requestId", requestId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await TouchAsync(connection, transaction, ownerUserId, conversationId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return message;
    }

    public async Task<bool> SetPersonaAsync(Guid ownerUserId, Guid conversationId, Guid personaId, CancellationToken cancellationToken = default)
    {
        // Migration 5 persists this column. Until then, accept the call so the interface compiles and only check ownership.
        RequireOwner(ownerUserId);
        RequirePersona(personaId);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM Conversations WHERE {OwnedConversation} LIMIT 1;";
        AddScope(command, ownerUserId, conversationId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<ConversationSummary> InsertConversationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid ownerUserId, string? title, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var summary = new ConversationSummary(Guid.NewGuid(), title, now, now, PersonaIds.BuiltInMira);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Conversations (Id, OwnerUserId, Title, CreatedAt, UpdatedAt)
            VALUES ($id, $owner, $title, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", summary.Id.ToString());
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return summary;
    }

    private static async Task<ConversationSnapshot> SnapshotAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid ownerUserId, ConversationSummary summary, CancellationToken cancellationToken)
    {
        var messages = await ReadMessagesAsync(connection, transaction, ownerUserId, summary.Id, cancellationToken);
        return new ConversationSnapshot(summary.Id, ownerUserId, summary.Title, summary.CreatedAt, summary.UpdatedAt, messages, summary.PersonaId);
    }

    private static async Task<IReadOnlyList<ConversationMessage>> ReadMessagesAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT Id, Role, Content, Status, CreatedAt, ModelId, RequestId FROM Messages
            WHERE ConversationId IN (SELECT Id FROM Conversations WHERE {OwnedConversation})
            ORDER BY CreatedAt, rowid;
            """;
        AddScope(command, ownerUserId, conversationId);
        var messages = new List<ConversationMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new ConversationMessage(
                Guid.Parse(reader.GetString(0)),
                Enum.Parse<ChatRole>(reader.GetString(1)),
                reader.GetString(2),
                Enum.Parse<MessageStatus>(reader.GetString(3)),
                MiraDatabase.ParseTime(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return messages;
    }

    private static async Task<bool> OwnsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT 1 FROM Conversations WHERE {OwnedConversation};";
        AddScope(command, ownerUserId, conversationId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task TouchAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid ownerUserId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE Conversations SET UpdatedAt = $now WHERE {OwnedConversation};";
        AddScope(command, ownerUserId, conversationId);
        command.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddScope(SqliteCommand command, Guid ownerUserId, Guid conversationId)
    {
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString());
    }

    private static ConversationSummary ReadSummary(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        MiraDatabase.ParseTime(reader.GetString(2)),
        MiraDatabase.ParseTime(reader.GetString(3)),
        PersonaIds.BuiltInMira);

    private static void RequireOwner(Guid ownerUserId)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("An authenticated owner is required.", nameof(ownerUserId));
    }

    private static void RequirePersona(Guid personaId)
    {
        if (personaId == Guid.Empty) throw new ArgumentException("A persona id is required.", nameof(personaId));
    }
}
