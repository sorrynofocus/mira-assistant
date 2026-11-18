using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Mira.Core;
using Mira.Infrastructure;
using Mira.Infrastructure.Sqlite;

namespace Mira.Tests;

// IUserPreferenceStore contract, run against every implementation.
public abstract class PreferenceStoreContractTests : IAsyncLifetime
{
    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));

    protected IUserPreferenceStore Store { get; private set; } = null!;

    public virtual async Task InitializeAsync() => Store = await CreateStoreAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    protected abstract Task<IUserPreferenceStore> CreateStoreAsync();

    protected abstract Task<Guid> CreateOwnerAsync();

    [Fact]
    public async Task DefaultsWithNoRows()
    {
        Assert.Equal(UserPreferences.Default, await Store.GetAsync(await CreateOwnerAsync()));
    }

    [Theory]
    [InlineData(ThemeValues.Sovereign, null, null)]
    [InlineData(ThemeValues.System, null, null)]
    [InlineData(null, FontSizeValues.Small, null)]
    [InlineData(null, FontSizeValues.Large, null)]
    [InlineData(null, null, true)]
    public async Task EachKeyRoundTrips(string? theme, string? fontSize, bool? inspector)
    {
        var owner = await CreateOwnerAsync();
        var update = new PreferenceUpdate(theme, fontSize, inspector);
        var expected = UserPreferences.Default.Apply(update);

        Assert.Equal(expected, await Store.UpdateAsync(owner, update));
        Assert.Equal(expected, await Store.GetAsync(owner));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShowInspectLinksRoundTrips(bool value)
    {
        var owner = await CreateOwnerAsync();
        var update = new PreferenceUpdate(ShowInspectLinks: value);
        var expected = UserPreferences.Default.Apply(update);

        Assert.Equal(expected, await Store.UpdateAsync(owner, update));
        Assert.Equal(expected, await Store.GetAsync(owner));
    }

    [Fact]
    public async Task PartialUpdatesKeepOtherKeysAndOverwrite()
    {
        var owner = await CreateOwnerAsync();
        await Store.UpdateAsync(owner, new PreferenceUpdate(ThemeValues.Sovereign, FontSizeValues.Large, true));
        await Store.UpdateAsync(owner, new PreferenceUpdate(FontSize: FontSizeValues.Small));
        await Store.UpdateAsync(owner, new PreferenceUpdate(InspectorOpenByDefault: false));
        await Store.UpdateAsync(owner, new PreferenceUpdate(ShowInspectLinks: false));

        Assert.Equal(new UserPreferences(ThemeValues.Sovereign, FontSizeValues.Small, false, false), await Store.GetAsync(owner));
    }

    [Fact]
    public async Task OwnersAreIsolated()
    {
        var alice = await CreateOwnerAsync();
        var bob = await CreateOwnerAsync();
        await Store.UpdateAsync(alice, new PreferenceUpdate(ThemeValues.Sovereign, FontSizeValues.Large, true));

        Assert.Equal(UserPreferences.Default, await Store.GetAsync(bob));
        await Store.UpdateAsync(bob, new PreferenceUpdate(Theme: ThemeValues.System));
        Assert.Equal(new UserPreferences(ThemeValues.Sovereign, FontSizeValues.Large, true, true), await Store.GetAsync(alice));
    }

    [Fact]
    public async Task InvalidCallsAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.UpdateAsync(Guid.Empty, new PreferenceUpdate(Theme: ThemeValues.System)));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.UpdateAsync(Guid.NewGuid(), new PreferenceUpdate()));
    }
}

public sealed class InMemoryPreferenceStoreTests : PreferenceStoreContractTests
{
    protected override Task<IUserPreferenceStore> CreateStoreAsync() => Task.FromResult<IUserPreferenceStore>(new InMemoryUserPreferenceStore());

    protected override Task<Guid> CreateOwnerAsync() => Task.FromResult(Guid.NewGuid());
}

public sealed class SqlitePreferenceStoreTests : PreferenceStoreContractTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mira-tests", Guid.NewGuid().ToString("N"));
    private MiraOptions _options = null!;
    private MiraDatabase _database = null!;
    private int _owners;

    protected override async Task<IUserPreferenceStore> CreateStoreAsync()
    {
        _options = new MiraOptions { DatabasePath = Path.Combine(_directory, "mira.db") };
        _database = new MiraDatabase(_options);
        await _database.MigrateAsync();
        return new SqliteUserPreferenceStore(_database, Time);
    }

    protected override async Task<Guid> CreateOwnerAsync() =>
        (await new SqliteUserAccountService(_database, _options, Time).CreateAsync($"owner{++_owners}", "synthetic fixture password")).Id;

    public override Task DisposeAsync()
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
    public async Task FreshDatabaseHasMigrationFourAndIsIdempotent()
    {
        Assert.Equal(4, MiraDatabase.LatestVersion);
        await _database.MigrateAsync();
        await using var connection = await _database.OpenAsync();
        Assert.Equal(4, await MiraDatabase.GetVersionAsync(connection));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'UserPreferences';";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task M2DatabaseUpgradesWithUsersSessionsAndConversationsIntact()
    {
        var options = new MiraOptions { DatabasePath = Path.Combine(_directory, "m2.db") };
        var database = new MiraDatabase(options);
        await database.MigrateAsync(3);
        var accounts = new SqliteUserAccountService(database, options, Time);
        var user = await accounts.CreateAsync("m2-owner", "an m2 era fixture password");
        var token = await new SqliteSessionStore(database, options, Time).CreateAsync(user.Id);
        var conversations = new SqliteConversationStore(database, Time);
        var conversation = await conversations.CreateAsync(user.Id, "kept");
        await conversations.AppendUserMessageAsync(user.Id, conversation.Id, "synthetic", "r1");

        await database.MigrateAsync();

        await using (var connection = await database.OpenAsync())
        {
            Assert.Equal(4, await MiraDatabase.GetVersionAsync(connection));
        }

        Assert.NotNull(await accounts.VerifyCredentialsAsync("m2-owner", "an m2 era fixture password"));
        Assert.Equal(user.Id, await new SqliteSessionStore(database, options, Time).ValidateAsync(token));
        var snapshot = await conversations.GetAsync(user.Id, conversation.Id);
        Assert.Equal("kept", snapshot!.Title);
        Assert.Single(snapshot.Messages);
        var preferences = new SqliteUserPreferenceStore(database, Time);
        Assert.Equal(UserPreferences.Default, await preferences.GetAsync(user.Id));
        await preferences.UpdateAsync(user.Id, new PreferenceUpdate(Theme: ThemeValues.Sovereign));
        Assert.Equal(ThemeValues.Sovereign, (await preferences.GetAsync(user.Id)).Theme);
    }

    [Fact]
    public async Task ValuesSurviveARestartAndRowsAreStoredAsAllowlistedText()
    {
        var owner = await CreateOwnerAsync();
        await Store.UpdateAsync(owner, new PreferenceUpdate(ThemeValues.System, FontSizeValues.Large, true));
        SqliteConnection.ClearAllPools();

        var reopened = new SqliteUserPreferenceStore(new MiraDatabase(_options), Time);
        Assert.Equal(new UserPreferences(ThemeValues.System, FontSizeValues.Large, true, true), await reopened.GetAsync(owner));

        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value, UpdatedAt FROM UserPreferences WHERE OwnerUserId = $owner ORDER BY Key;";
        command.Parameters.AddWithValue("$owner", owner.ToString());
        var rows = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add($"{reader.GetString(0)}={reader.GetString(1)}");
                Assert.Equal(Time.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture), reader.GetString(2));
            }
        }

        Assert.Equal(["fontSize=large", "inspectorOpenByDefault=true", "theme=system"], rows);
    }

    [Fact]
    public async Task InvalidStoredRowsFallBackToDefaultsAndLongValuesAreRejected()
    {
        var owner = await CreateOwnerAsync();
        await using var connection = await _database.OpenAsync();
        foreach (var (key, value) in new[] { ("theme", "neon"), ("fontSize", "LARGE"), ("inspectorOpenByDefault", "yes"), ("showInspectLinks", "yes"), ("unknown", "x") })
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO UserPreferences (OwnerUserId, Key, Value, UpdatedAt) VALUES ($owner, $key, $value, '2026-11-17T09:00:00.0000000Z');";
            insert.Parameters.AddWithValue("$owner", owner.ToString());
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value);
            await insert.ExecuteNonQueryAsync();
        }

        Assert.Equal(UserPreferences.Default, await Store.GetAsync(owner));

        await using var tooLong = connection.CreateCommand();
        tooLong.CommandText = "INSERT INTO UserPreferences (OwnerUserId, Key, Value, UpdatedAt) VALUES ($owner, 'long', $value, '2026-11-17T09:00:00.0000000Z');";
        tooLong.Parameters.AddWithValue("$owner", owner.ToString());
        tooLong.Parameters.AddWithValue("$value", new string('x', 65));
        await Assert.ThrowsAsync<SqliteException>(() => tooLong.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task UnknownOwnerViolatesTheForeignKey()
    {
        await Assert.ThrowsAsync<SqliteException>(() => Store.UpdateAsync(Guid.NewGuid(), new PreferenceUpdate(Theme: ThemeValues.System)));
    }
}
