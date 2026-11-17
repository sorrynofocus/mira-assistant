using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mira.Core;
using Mira.Infrastructure.Pipeline;

namespace Mira.Infrastructure.Sqlite;

// SQLite IPipelineEventStore (M2). RedactedPayload holds only a serialized PipelineEventData (the allowlist); reads are
// owner-scoped. Parameterized SQL only. Event order is CreatedAt, then insertion order (rowid).
public sealed class SqlitePipelineEventStore(MiraDatabase database) : IPipelineEventStore
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task AppendAsync(PipelineEvent pipelineEvent, CancellationToken cancellationToken = default)
    {
        InMemoryPipelineEventStore.Validate(pipelineEvent);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Atomic guard for permanent delete: an event for a conversation that no longer exists (for this owner) is dropped,
        // so a turn still running when its conversation is deleted can't leave events behind.
        command.CommandText = """
            INSERT INTO PipelineEvents (Id, OwnerUserId, RequestId, ConversationId, EventType, ElapsedMs, RedactedPayload, CreatedAt)
            SELECT $id, $owner, $requestId, $conversationId, $eventType, $elapsedMs, $payload, $createdAt
            WHERE $conversationId IS NULL
               OR EXISTS (SELECT 1 FROM Conversations WHERE Id = $conversationId AND OwnerUserId = $owner);
            """;
        command.Parameters.AddWithValue("$id", pipelineEvent.Id.ToString());
        command.Parameters.AddWithValue("$owner", pipelineEvent.OwnerUserId.ToString());
        command.Parameters.AddWithValue("$requestId", pipelineEvent.RequestId);
        command.Parameters.AddWithValue("$conversationId", (object?)pipelineEvent.ConversationId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$eventType", pipelineEvent.EventType);
        command.Parameters.AddWithValue("$elapsedMs", pipelineEvent.ElapsedMs);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(pipelineEvent.Data, PayloadJson));
        command.Parameters.AddWithValue("$createdAt", MiraDatabase.FormatTime(pipelineEvent.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PipelineEvent>> GetByRequestAsync(Guid ownerUserId, string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(requestId)) return [];
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OwnerUserId, RequestId, ConversationId, EventType, ElapsedMs, RedactedPayload, CreatedAt FROM PipelineEvents
            WHERE OwnerUserId = $owner AND RequestId = $requestId
            ORDER BY CreatedAt, rowid;
            """;
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);
        return await ReadEventsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<PipelineRequestSummary>> ListRecentRequestsAsync(Guid ownerUserId, Guid? conversationId, int take, CancellationToken cancellationToken = default)
    {
        InMemoryPipelineEventStore.ValidateTake(take);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Pick the newest requests first (bounded), then load just their events and summarize them in code.
        command.CommandText = """
            WITH Recent AS (
                SELECT RequestId FROM PipelineEvents
                WHERE OwnerUserId = $owner AND ($conversationId IS NULL OR ConversationId = $conversationId)
                GROUP BY RequestId
                ORDER BY MIN(CreatedAt) DESC, RequestId
                LIMIT $take
            )
            SELECT Id, OwnerUserId, RequestId, ConversationId, EventType, ElapsedMs, RedactedPayload, CreatedAt FROM PipelineEvents
            WHERE OwnerUserId = $owner AND ($conversationId IS NULL OR ConversationId = $conversationId)
              AND RequestId IN (SELECT RequestId FROM Recent)
            ORDER BY CreatedAt, rowid;
            """;
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        command.Parameters.AddWithValue("$conversationId", (object?)conversationId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$take", take);
        return InMemoryPipelineEventStore.Summarize(await ReadEventsAsync(command, cancellationToken), take);
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PipelineEvents WHERE CreatedAt < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", MiraDatabase.FormatTime(cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<PipelineEvent>> ReadEventsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var events = new List<PipelineEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var data = reader.IsDBNull(6)
                ? new PipelineEventData()
                : JsonSerializer.Deserialize<PipelineEventData>(reader.GetString(6), PayloadJson) ?? new PipelineEventData();
            events.Add(new PipelineEvent(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetInt64(5),
                data,
                MiraDatabase.ParseTime(reader.GetString(7))));
        }

        return events;
    }
}
