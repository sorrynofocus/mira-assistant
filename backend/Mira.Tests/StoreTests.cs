using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Mira.Api.Auth;
using Mira.Core;
using Mira.Infrastructure;
using Mira.Infrastructure.Sqlite;

namespace Mira.Tests;

public sealed class StoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mira-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    private MiraOptions _options = null!;
    private MiraDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _options = new MiraOptions { DatabasePath = Path.Combine(_directory, "mira.db") };
        _database = new MiraDatabase(_options);
        await _database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task MigrationsAreIdempotentAndVersioned()
    {
        await _database.MigrateAsync();
        await using var connection = await _database.OpenAsync();
        Assert.Equal(MiraDatabase.LatestVersion, await MiraDatabase.GetVersionAsync(connection));
    }

    [Fact]
    public async Task PasswordHashIsSaltedAndNeverPlaintext()
    {
        var accounts = new SqliteUserAccountService(_database, _options, _time);
        await accounts.CreateAsync("one", "same password for both users");
        await accounts.CreateAsync("two", "same password for both users");

        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PasswordHash FROM Users;";
        var hashes = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) hashes.Add(reader.GetString(0));
        }

        Assert.Equal(2, hashes.Count);
        Assert.NotEqual(hashes[0], hashes[1]);
        Assert.DoesNotContain(hashes, h => h.Contains("same password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsernamesAreCaseInsensitiveAndUnique()
    {
        var accounts = new SqliteUserAccountService(_database, _options, _time);
        await accounts.CreateAsync("Chris", "password one two three");

        await Assert.ThrowsAsync<AccountValidationException>(() => accounts.CreateAsync("CHRIS", "another password here"));
        Assert.NotNull(await accounts.VerifyCredentialsAsync("  chris ", "password one two three"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public async Task InvalidUsernamesAreRejected(string username)
    {
        var accounts = new SqliteUserAccountService(_database, _options, _time);
        await Assert.ThrowsAsync<AccountValidationException>(() => accounts.CreateAsync(username, "a long enough password"));
    }

    [Fact]
    public async Task PasswordLimitsAreEnforcedOnCreate()
    {
        var accounts = new SqliteUserAccountService(_database, _options, _time);
        await Assert.ThrowsAsync<AccountValidationException>(() => accounts.CreateAsync("short", "tiny"));
        await Assert.ThrowsAsync<AccountValidationException>(() => accounts.CreateAsync("huge", new string('x', _options.Auth.MaximumPasswordLength + 1)));
        await accounts.CreateAsync("max", new string('x', _options.Auth.MaximumPasswordLength));
    }

    [Fact]
    public async Task EquivalentUnicodeFormsVerify()
    {
        var accounts = new SqliteUserAccountService(_database, _options, _time);
        await accounts.CreateAsync("unicode", "café au lait with extra length");

        // Decomposed e + combining acute accent, as some keyboards produce it.
        Assert.NotNull(await accounts.VerifyCredentialsAsync("unicode", "café au lait with extra length"));
    }

    [Fact]
    public async Task SessionsExpireAfterIdleTimeout()
    {
        var (sessions, userId) = await SessionFixtureAsync();
        var token = await sessions.CreateAsync(userId);

        _time.Advance(TimeSpan.FromMinutes(_options.Auth.SessionIdleMinutes - 1));
        Assert.Equal(userId, await sessions.ValidateAsync(token));

        _time.Advance(TimeSpan.FromMinutes(_options.Auth.SessionIdleMinutes + 1));
        Assert.Null(await sessions.ValidateAsync(token));
    }

    [Fact]
    public async Task ActivityExtendsTheIdleWindowButNotTheAbsoluteLifetime()
    {
        var (sessions, userId) = await SessionFixtureAsync();
        var token = await sessions.CreateAsync(userId);

        var elapsed = TimeSpan.Zero;
        var step = TimeSpan.FromMinutes(_options.Auth.SessionIdleMinutes - 5);
        while (elapsed + step < TimeSpan.FromHours(_options.Auth.SessionAbsoluteHours))
        {
            _time.Advance(step);
            elapsed += step;
            Assert.Equal(userId, await sessions.ValidateAsync(token));
        }

        _time.Advance(step);
        Assert.Null(await sessions.ValidateAsync(token));
    }

    [Fact]
    public async Task RevokedAndUnknownSessionsAreRejected()
    {
        var (sessions, userId) = await SessionFixtureAsync();
        var first = await sessions.CreateAsync(userId);
        var second = await sessions.CreateAsync(userId);

        await sessions.RevokeAsync(first);
        Assert.Null(await sessions.ValidateAsync(first));
        Assert.Equal(userId, await sessions.ValidateAsync(second));

        await sessions.RevokeAllAsync(userId);
        Assert.Null(await sessions.ValidateAsync(second));
        Assert.Null(await sessions.ValidateAsync("made-up-token"));
        Assert.Null(await sessions.ValidateAsync(new string('a', 5000)));
    }

    [Fact]
    public async Task SessionTokensAreStoredOnlyAsHashes()
    {
        var (sessions, userId) = await SessionFixtureAsync();
        var token = await sessions.CreateAsync(userId);

        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Sessions;";
        var stored = (string)(await command.ExecuteScalarAsync())!;
        Assert.NotEqual(token, stored);
        Assert.DoesNotContain(token, stored);
    }

    [Fact]
    public async Task GovernanceDeniesAndAuditsEveryDecision()
    {
        var audit = new SqliteGovernanceAuditLog(_database);
        IGovernanceService governance = new DenyAllGovernanceService(audit, _time);
        var owner = Guid.NewGuid();

        var write = await governance.EvaluateAsync(new CapabilityRequest(owner, "web", ActionClass.Write, "example.com"));
        var read = await governance.EvaluateAsync(new CapabilityRequest(owner, "search", ActionClass.Read, "public-web"));

        Assert.False(write.Allowed);
        Assert.False(read.Allowed);
        var records = await audit.ListForOwnerAsync(owner, 10);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.False(r.Allowed));
        Assert.Contains(records, r => r.Capability == "web" && r.Action == ActionClass.Write);
        Assert.Empty(await audit.ListForOwnerAsync(Guid.NewGuid(), 10));
    }

    [Fact]
    public async Task ConversationStoreIsScopedByOwner()
    {
        var store = new InMemoryConversationStore(_time);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await store.AppendUserMessageAsync(alice, "hello");
        var reply = await store.BeginAssistantMessageAsync(alice, "mock");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.FinishAssistantMessageAsync(bob, reply.Id, "hijack", MessageStatus.Completed));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetOrCreateAsync(Guid.Empty));
        Assert.Empty((await store.GetOrCreateAsync(bob)).Messages);
    }

    [Fact]
    public async Task InterruptedRepliesAreExcludedFromPromptContext()
    {
        var store = new InMemoryConversationStore(_time);
        var owner = Guid.NewGuid();
        await store.AppendUserMessageAsync(owner, "first");
        var reply = await store.BeginAssistantMessageAsync(owner, "mock");
        await store.FinishAssistantMessageAsync(owner, reply.Id, "half an ans", MessageStatus.Interrupted);

        var turns = await store.GetPromptTurnsAsync(owner);
        Assert.Equal([new ChatTurn(ChatRole.User, "first")], turns);
    }

    [Fact]
    public void LoginThrottleWindowResets()
    {
        var options = new MiraOptions();
        options.Auth.MaxFailedLoginsPerUsername = 2;
        var throttle = new LoginThrottle(options, _time);

        throttle.RecordFailure("OWNER");
        throttle.RecordFailure("OWNER");
        Assert.True(throttle.IsLockedOut("OWNER"));
        Assert.False(throttle.IsLockedOut("OTHER"));

        _time.Advance(TimeSpan.FromMinutes(options.Auth.FailedLoginWindowMinutes));
        Assert.False(throttle.IsLockedOut("OWNER"));
    }

    private async Task<(SqliteSessionStore Sessions, Guid UserId)> SessionFixtureAsync()
    {
        var accounts = new SqliteUserAccountService(_database, _options, _time);
        var user = await accounts.CreateAsync("session-owner", "session test password");
        return (new SqliteSessionStore(_database, _options, _time), user.Id);
    }
}
