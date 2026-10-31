using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Mira.Core;

namespace Mira.Infrastructure.Sqlite;

public sealed class SqliteSessionStore(MiraDatabase database, MiraOptions options, TimeProvider time) : ISessionStore
{
    private const int TokenBytes = 32;
    private const int MaxTokenLength = 64;
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public async Task<string> CreateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        var now = time.GetUtcNow();

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sessions (Id, UserId, CreatedAt, LastSeenAt, ExpiresAt, RevokedAt)
            VALUES ($id, $userId, $now, $now, $expiresAt, NULL);
            """;
        command.Parameters.AddWithValue("$id", Hash(token));
        command.Parameters.AddWithValue("$userId", userId.ToString());
        command.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(now));
        command.Parameters.AddWithValue("$expiresAt", MiraDatabase.FormatTime(now.AddHours(options.Auth.SessionAbsoluteHours)));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return token;
    }

    public async Task<Guid?> ValidateAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionToken) || sessionToken.Length > MaxTokenLength) return null;

        var id = Hash(sessionToken);
        var now = time.GetUtcNow();

        await using var connection = await database.OpenAsync(cancellationToken);
        Guid userId;
        DateTimeOffset lastSeen;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT s.UserId, s.LastSeenAt, s.ExpiresAt, s.RevokedAt, u.DisabledAt
                FROM Sessions s JOIN Users u ON u.Id = s.UserId
                WHERE s.Id = $id;
                """;
            query.Parameters.AddWithValue("$id", id);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            if (!reader.IsDBNull(3) || !reader.IsDBNull(4)) return null;
            if (now >= MiraDatabase.ParseTime(reader.GetString(2))) return null;

            userId = Guid.Parse(reader.GetString(0));
            lastSeen = MiraDatabase.ParseTime(reader.GetString(1));
        }

        if (now - lastSeen > TimeSpan.FromMinutes(options.Auth.SessionIdleMinutes))
        {
            await RevokeByIdAsync(id, cancellationToken);
            return null;
        }

        if (now - lastSeen >= TouchInterval)
        {
            await using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE Sessions SET LastSeenAt = $now WHERE Id = $id AND RevokedAt IS NULL;";
            touch.Parameters.AddWithValue("$id", id);
            touch.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(now));
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        return userId;
    }

    public Task RevokeAsync(string sessionToken, CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(sessionToken) || sessionToken.Length > MaxTokenLength
            ? Task.CompletedTask
            : RevokeByIdAsync(Hash(sessionToken), cancellationToken);

    public async Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Sessions SET RevokedAt = $now WHERE UserId = $userId AND RevokedAt IS NULL;";
        command.Parameters.AddWithValue("$userId", userId.ToString());
        command.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(time.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RevokeByIdAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Sessions SET RevokedAt = $now WHERE Id = $id AND RevokedAt IS NULL;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", MiraDatabase.FormatTime(time.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Only a hash of the token is stored, so a copied database cannot be used to forge session cookies.
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
