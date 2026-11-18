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
            """),
        (2, """
            CREATE TABLE Conversations (
                Id TEXT NOT NULL PRIMARY KEY,
                OwnerUserId TEXT NOT NULL REFERENCES Users(Id),
                Title TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IX_Conversations_Owner_UpdatedAt ON Conversations(OwnerUserId, UpdatedAt DESC);

            CREATE TABLE Messages (
                Id TEXT NOT NULL PRIMARY KEY,
                ConversationId TEXT NOT NULL REFERENCES Conversations(Id) ON DELETE CASCADE,
                Role TEXT NOT NULL,
                Content TEXT NOT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ModelId TEXT NULL,
                RequestId TEXT NULL
            );
            CREATE INDEX IX_Messages_Conversation_CreatedAt ON Messages(ConversationId, CreatedAt);
            """),
        (3, """
            CREATE TABLE PipelineEvents (
                Id TEXT NOT NULL PRIMARY KEY,
                OwnerUserId TEXT NOT NULL REFERENCES Users(Id),
                RequestId TEXT NOT NULL,
                ConversationId TEXT NULL,
                EventType TEXT NOT NULL,
                ElapsedMs INTEGER NOT NULL,
                RedactedPayload TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IX_PipelineEvents_Owner_RequestId ON PipelineEvents(OwnerUserId, RequestId);
            CREATE INDEX IX_PipelineEvents_CreatedAt ON PipelineEvents(CreatedAt);
            """),
        // M3: per-user preferences as key/value rows (only allowlisted keys and values are written; a missing row = default).
        // The length CHECKs are a backstop to the allowlist (MIRA Security 2026-11-17, nit 2).
        (4, """
            CREATE TABLE UserPreferences (
                OwnerUserId TEXT NOT NULL REFERENCES Users(Id),
                Key TEXT NOT NULL CHECK (length(Key) <= 64),
                Value TEXT NOT NULL CHECK (length(Value) <= 64),
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (OwnerUserId, Key)
            ) WITHOUT ROWID;
            """),
        // M4: personas. Name, description and body live in PersonaVersions (at most PersonaLimits.MaxVersionsPerPersona kept).
        // Conversations.PersonaId is a soft pin: no REFERENCES, so deleting a persona leaves the pin in place (UI: "deleted").
        // The built-in MIRA row (OwnerUserId NULL, fixed id PersonaIds.BuiltInMira) is seeded here. Its text must match
        // BuiltInPersona.Markdown (tested); a later wording change needs a new migration, never an edit here.
        (5, """
            CREATE TABLE Personas (
                Id TEXT NOT NULL PRIMARY KEY,
                OwnerUserId TEXT NULL REFERENCES Users(Id),
                ActiveVersion INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IX_Personas_OwnerUserId ON Personas(OwnerUserId);

            CREATE TABLE PersonaVersions (
                PersonaId TEXT NOT NULL REFERENCES Personas(Id) ON DELETE CASCADE,
                Version INTEGER NOT NULL,
                Name TEXT NOT NULL CHECK (length(Name) BETWEEN 1 AND 80),
                Description TEXT NULL CHECK (length(Description) <= 200),
                Markdown TEXT NOT NULL CHECK (length(Markdown) BETWEEN 1 AND 8000),
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY (PersonaId, Version)
            );

            CREATE TABLE UserPersonaSettings (
                OwnerUserId TEXT NOT NULL PRIMARY KEY REFERENCES Users(Id),
                DefaultPersonaId TEXT NOT NULL REFERENCES Personas(Id),
                UpdatedAt TEXT NOT NULL
            );

            ALTER TABLE Conversations ADD COLUMN PersonaId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001';
            UPDATE Conversations SET PersonaId = '00000000-0000-0000-0000-000000000001';

            INSERT INTO Personas (Id, OwnerUserId, ActiveVersion, CreatedAt, UpdatedAt)
            VALUES ('00000000-0000-0000-0000-000000000001', NULL, 1, '2026-11-18T00:00:00.0000000Z', '2026-11-18T00:00:00.0000000Z');
            INSERT INTO PersonaVersions (PersonaId, Version, Name, Description, Markdown, CreatedAt)
            VALUES ('00000000-0000-0000-0000-000000000001', 1, 'MIRA', 'Helpful, candid and concise.',
                    '# MIRA

            ## Identity
            You are MIRA (Memory, Intelligence, Reasoning and Autonomy), a local assistant.

            ## Style
            - Helpful, candid and concise.
            - Say plainly when you do not know something.

            ## Boundaries
            - Never claim to have taken an action, called a tool or remembered something that did not happen.
            - Do not invent memories, sources or facts.',
                    '2026-11-18T00:00:00.0000000Z');
            """)
    ];

    // Version that introduced the Messages table (needed by the startup sweep).
    private const int MessagesVersion = 2;

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

    // Applies every pending migration. Shared by the API host and the create-user command; it changes no rows.
    public Task MigrateAsync(CancellationToken cancellationToken = default) =>
        MigrateAsync(LatestVersion, cancellationToken);

    // Applies pending migrations up to targetVersion. Used directly only by upgrade tests (for example to build an M1-shaped database).
    public async Task MigrateAsync(int targetVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetVersion, LatestVersion);

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
            if (version <= current || version > targetVersion) continue;

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

    // API startup only (MiraHost.InitializeMiraDatabaseAsync), after MigrateAsync and before requests are served: a message still
    // Streaming was left by a crash or shutdown mid-turn, so it becomes Interrupted (and stays out of prompt context).
    // Not run by create-user, which may execute while the API is serving a live turn. Returns the number of messages changed.
    public async Task<int> InterruptStreamingMessagesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (await GetVersionAsync(connection, cancellationToken) < MessagesVersion) return 0;

        await using var sweep = connection.CreateCommand();
        sweep.CommandText = "UPDATE Messages SET Status = $interrupted WHERE Status = $streaming;";
        sweep.Parameters.AddWithValue("$interrupted", nameof(MessageStatus.Interrupted));
        sweep.Parameters.AddWithValue("$streaming", nameof(MessageStatus.Streaming));
        return await sweep.ExecuteNonQueryAsync(cancellationToken);
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
