using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Mira.Core;

namespace Mira.Infrastructure.Sqlite;

public sealed class SqliteUserAccountService(MiraDatabase database, MiraOptions options, TimeProvider time) : IUserAccountService
{
    private const int SqliteConstraintError = 19;

    // PBKDF2 (HMAC-SHA512, salted, versioned) from ASP.NET Core Identity. The user argument is unused by the default hasher.
    private static readonly PasswordHasher<UserAccount> Hasher = new();
    private static readonly UserAccount Placeholder = new(Guid.Empty, string.Empty, DateTimeOffset.MinValue, null);

    // Verified against when no real hash is available so unknown users cost the same time as wrong passwords.
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(Placeholder, Guid.NewGuid().ToString("N")));

    public async Task<UserAccount> CreateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var cleaned = UsernameRules.Clean(username);
        UsernameRules.Validate(cleaned);
        ArgumentNullException.ThrowIfNull(password);
        var normalizedPassword = PasswordRules.Normalize(password);
        if (normalizedPassword.Length < options.Auth.MinimumPasswordLength || normalizedPassword.Length > options.Auth.MaximumPasswordLength)
        {
            throw new AccountValidationException(
                $"Password must be {options.Auth.MinimumPasswordLength}-{options.Auth.MaximumPasswordLength} characters.");
        }

        var account = new UserAccount(Guid.NewGuid(), cleaned, time.GetUtcNow(), null);
        var hash = Hasher.HashPassword(account, normalizedPassword);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Users (Id, Username, NormalizedUsername, PasswordHash, CreatedAt, DisabledAt)
            VALUES ($id, $username, $normalized, $hash, $createdAt, NULL);
            """;
        command.Parameters.AddWithValue("$id", account.Id.ToString());
        command.Parameters.AddWithValue("$username", cleaned);
        command.Parameters.AddWithValue("$normalized", UsernameRules.NormalizeKey(cleaned));
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$createdAt", MiraDatabase.FormatTime(account.CreatedAt));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintError)
        {
            throw new AccountValidationException("That username is already taken.");
        }

        return account;
    }

    public async Task<UserAccount?> VerifyCredentialsAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password) || password.Length > options.Auth.MaximumPasswordLength)
        {
            Hasher.VerifyHashedPassword(Placeholder, DummyHash.Value, "placeholder");
            return null;
        }

        var normalizedPassword = PasswordRules.Normalize(password);
        var stored = await FindByNormalizedUsernameAsync(UsernameRules.NormalizeKey(username), cancellationToken);
        if (stored is null)
        {
            Hasher.VerifyHashedPassword(Placeholder, DummyHash.Value, normalizedPassword);
            return null;
        }

        var (account, hash) = stored.Value;
        var result = Hasher.VerifyHashedPassword(account, hash, normalizedPassword);
        if (result == PasswordVerificationResult.Failed || account.DisabledAt is not null) return null;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await UpdateHashAsync(account.Id, Hasher.HashPassword(account, normalizedPassword), cancellationToken);
        }

        return account;
    }

    public async Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Username, CreatedAt, DisabledAt FROM Users WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAccount(reader) : null;
    }

    public async Task SetDisabledAsync(Guid id, bool disabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Users SET DisabledAt = $disabledAt WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$disabledAt", disabled ? MiraDatabase.FormatTime(time.GetUtcNow()) : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(UserAccount Account, string Hash)?> FindByNormalizedUsernameAsync(string normalized, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Username, CreatedAt, DisabledAt, PasswordHash FROM Users WHERE NormalizedUsername = $normalized;";
        command.Parameters.AddWithValue("$normalized", normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (ReadAccount(reader), reader.GetString(4));
    }

    private async Task UpdateHashAsync(Guid id, string hash, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Users SET PasswordHash = $hash WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$hash", hash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static UserAccount ReadAccount(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        MiraDatabase.ParseTime(reader.GetString(2)),
        MiraDatabase.ParseNullableTime(reader.GetValue(3)));
}
