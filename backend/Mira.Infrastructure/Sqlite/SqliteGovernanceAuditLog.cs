using Mira.Core;

namespace Mira.Infrastructure.Sqlite;

public sealed class SqliteGovernanceAuditLog(MiraDatabase database) : IGovernanceAuditLog
{
    public async Task RecordAsync(GovernanceAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActionAudit (Id, OwnerUserId, GrantId, Capability, Action, ResourceScope, Decision, RedactedSummary, CreatedAt)
            VALUES ($id, $owner, $grant, $capability, $action, $scope, $decision, $summary, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$owner", record.OwnerUserId.ToString());
        command.Parameters.AddWithValue("$grant", record.GrantId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$capability", record.Capability);
        command.Parameters.AddWithValue("$action", record.Action.ToString());
        command.Parameters.AddWithValue("$scope", record.ResourceScope);
        command.Parameters.AddWithValue("$decision", record.Allowed ? "Allowed" : "Denied");
        command.Parameters.AddWithValue("$summary", record.RedactedSummary);
        command.Parameters.AddWithValue("$createdAt", MiraDatabase.FormatTime(record.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GovernanceAuditRecord>> ListForOwnerAsync(Guid ownerUserId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OwnerUserId, GrantId, Capability, Action, ResourceScope, Decision, RedactedSummary, CreatedAt
            FROM ActionAudit WHERE OwnerUserId = $owner ORDER BY CreatedAt DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$owner", ownerUserId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var records = new List<GovernanceAuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new GovernanceAuditRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                Enum.Parse<ActionClass>(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6) == "Allowed",
                reader.GetString(7),
                MiraDatabase.ParseTime(reader.GetString(8))));
        }

        return records;
    }
}
