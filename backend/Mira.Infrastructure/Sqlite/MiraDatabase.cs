using System.Globalization;
using Microsoft.Data.Sqlite;
using Mira.Core;

namespace Mira.Infrastructure.Sqlite;

public sealed class MiraDatabase
{
    // Append-only. Never edit an applied migration; add a new version instead.
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            CREATE TABLE Users (
                Id TEXT NOT NULL PRIMARY KEY,
                Username TEXT NOT NULL,
                NormalizedUsername TEXT NOT NULL UNIQUE,
                PasswordHash TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                DisabledAt TEXT NULL
            );

            CREATE TABLE Sessions (
                Id TEXT NOT NULL PRIMARY KEY,
                UserId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                CreatedAt TEXT NOT NULL,
                LastSeenAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                RevokedAt TEXT NULL
            );
            CREATE INDEX IX_Sessions_UserId ON Sessions(UserId);

            CREATE TABLE ActionAudit (
                Id TEXT NOT NULL PRIMARY KEY,
                OwnerUserId TEXT NOT NULL,
                GrantId TEXT NULL,
                Capability TEXT NOT NULL,
                Action TEXT NOT NULL,
                ResourceScope TEXT NOT NULL,
                Decision TEXT NOT NULL,
                RedactedSummary TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IX_ActionAudit_Owner_CreatedAt ON ActionAudit(OwnerUserId, CreatedAt);
            """)
    ];

    private readonly string _connectionString;

    public MiraDatabase(MiraOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        FilePath = options.ResolveDatabasePath();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public string FilePath { get; }

    public static int LatestVersion => Migrations[^1].Version;

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteAsync(connection, null,
            "CREATE TABLE IF NOT EXISTS SchemaMigrations (Version INTEGER NOT NULL PRIMARY KEY, AppliedAt TEXT NOT NULL);",
            cancellationToken);

        var current = await GetVersionAsync(connection, cancellationToken);
        foreach (var (version, sql) in Migrations)
        {
            if (version <= current) continue;

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(connection, transaction, sql, cancellationToken);
            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO SchemaMigrations (Version, AppliedAt) VALUES ($version, $appliedAt);";
            record.Parameters.AddWithValue("$version", version);
            record.Parameters.AddWithValue("$appliedAt", FormatTime(DateTimeOffset.UtcNow));
            await record.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    internal static string FormatTime(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    internal static DateTimeOffset? ParseNullableTime(object value) =>
        value is string text ? ParseTime(text) : null;

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
