using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Mira.Core;
using Mira.Infrastructure;
using Mira.Infrastructure.Sqlite;

namespace Mira.Tests;

// IPersonaStore contract, run against every implementation. Fixture text is synthetic.
public abstract class PersonaStoreContractTests : IAsyncLifetime
{
    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 18, 1, 0, 0, TimeSpan.Zero));

    protected IPersonaStore Store { get; private set; } = null!;

    public virtual async Task InitializeAsync() => Store = await CreateStoreAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    protected abstract Task<IPersonaStore> CreateStoreAsync();

    protected abstract Task<Guid> CreateOwnerAsync();

    [Fact]
    public async Task BuiltInIsListedFirstAndReadable()
    {
        var owner = await CreateOwnerAsync();
        var mine = await Store.CreateAsync(owner, "Jack", null, "Be brief.");

        var list = await Store.ListAsync(owner);
        Assert.Equal([PersonaIds.BuiltInMira, mine.Id], list.Select(p => p.Id));
        Assert.True(list[0].IsBuiltIn);
        Assert.Null(list[0].OwnerUserId);
        Assert.False(list[1].IsBuiltIn);

        var builtIn = await Store.GetAsync(owner, PersonaIds.BuiltInMira);
        Assert.Equal(BuiltInPersona.Name, builtIn!.Name);
        Assert.Equal(BuiltInPersona.Description, builtIn.Description);
        Assert.Equal(BuiltInPersona.Markdown, builtIn.Markdown);
        Assert.Equal(1, builtIn.ActiveVersion);
        Assert.Single(await Store.ListVersionsAsync(owner, PersonaIds.BuiltInMira));
        Assert.Equal(BuiltInPersona.Markdown, (await Store.GetVersionAsync(owner, PersonaIds.BuiltInMira, 1))!.Markdown);
        Assert.False(await Store.DeleteAsync(owner, PersonaIds.BuiltInMira));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.UpdateAsync(owner, PersonaIds.BuiltInMira, "x", null, "y"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.RevertAsync(owner, PersonaIds.BuiltInMira, 1));
    }

    [Fact]
    public async Task VersionsCarryNameDescriptionAndBodyAndRevertRestoresAllThree()
    {
        var owner = await CreateOwnerAsync();
        var created = await Store.CreateAsync(owner, "  Jack  ", "First take", "Body one");
        Assert.Equal(("Jack", "First take", "Body one", 1), (created.Name, created.Description, created.Markdown, created.ActiveVersion));

        Time.Advance(TimeSpan.FromMinutes(1));
        var updated = await Store.UpdateAsync(owner, created.Id, "Jackie", null, "Body two");
        Assert.Equal(("Jackie", (string?)null, "Body two", 2), (updated.Name, updated.Description, updated.Markdown, updated.ActiveVersion));
        Assert.Equal(Time.GetUtcNow(), updated.UpdatedAt);

        var versions = await Store.ListVersionsAsync(owner, created.Id);
        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.Equal("First take", versions[1].Description);
        Assert.Equal("Body one", (await Store.GetVersionAsync(owner, created.Id, 1))!.Markdown);

        var reverted = await Store.RevertAsync(owner, created.Id, 1);
        Assert.Equal(("Jack", "First take", "Body one", 1), (reverted.Name, reverted.Description, reverted.Markdown, reverted.ActiveVersion));
        Assert.Equal(reverted, await Store.GetAsync(owner, created.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.RevertAsync(owner, created.Id, 99));
    }

    [Fact]
    public async Task SavesPruneToFiveVersionsKeepingTheActiveOne()
    {
        var owner = await CreateOwnerAsync();
        var persona = await Store.CreateAsync(owner, "v1", null, "b1");
        for (var i = 2; i <= 7; i++) await Store.UpdateAsync(owner, persona.Id, $"v{i}", null, $"b{i}");

        Assert.Equal([7, 6, 5, 4, 3], (await Store.ListVersionsAsync(owner, persona.Id)).Select(v => v.Version));
        Assert.Null(await Store.GetVersionAsync(owner, persona.Id, 2));

        // Revert does not prune; the next save does, and the new version is the active one.
        await Store.RevertAsync(owner, persona.Id, 3);
        Assert.Equal(5, (await Store.ListVersionsAsync(owner, persona.Id)).Count);
        var saved = await Store.UpdateAsync(owner, persona.Id, "v8", null, "b8");
        Assert.Equal(8, saved.ActiveVersion);
        Assert.Equal([8, 7, 6, 5, 4], (await Store.ListVersionsAsync(owner, persona.Id)).Select(v => v.Version));
    }

    [Fact]
    public async Task TwentyPersonaCapIsEnforced()
    {
        var owner = await CreateOwnerAsync();
        for (var i = 0; i < PersonaLimits.MaxUserPersonas; i++) await Store.CreateAsync(owner, $"p{i}", null, "body");

        await Assert.ThrowsAsync<PersonaConflictException>(() => Store.CreateAsync(owner, "one too many", null, "body"));
        Assert.Equal(PersonaLimits.MaxUserPersonas + 1, (await Store.ListAsync(owner)).Count); // + built-in
        await Store.CreateAsync(await CreateOwnerAsync(), "other owner unaffected", null, "body");
    }

    [Fact]
    public async Task DefaultIsBuiltInUntilSetAndOnlyOwnedOrBuiltInIsAllowed()
    {
        var alice = await CreateOwnerAsync();
        var bob = await CreateOwnerAsync();
        var mine = await Store.CreateAsync(alice, "Jack", null, "body");
        var theirs = await Store.CreateAsync(bob, "Theirs", null, "body");

        Assert.Equal(PersonaIds.BuiltInMira, await Store.GetDefaultAsync(alice));
        await Store.SetDefaultAsync(alice, mine.Id);
        Assert.Equal(mine.Id, await Store.GetDefaultAsync(alice));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.SetDefaultAsync(alice, theirs.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.SetDefaultAsync(alice, Guid.NewGuid()));
        Assert.Equal(mine.Id, await Store.GetDefaultAsync(alice));
        await Store.SetDefaultAsync(alice, PersonaIds.BuiltInMira);
        Assert.Equal(PersonaIds.BuiltInMira, await Store.GetDefaultAsync(alice));
        Assert.Equal(PersonaIds.BuiltInMira, await Store.GetDefaultAsync(bob));
    }

    [Fact]
    public async Task DeletingTheDefaultResetsItToBuiltIn()
    {
        var owner = await CreateOwnerAsync();
        var persona = await Store.CreateAsync(owner, "Jack", null, "body");
        await Store.UpdateAsync(owner, persona.Id, "Jack", null, "body 2");
        await Store.SetDefaultAsync(owner, persona.Id);

        Assert.True(await Store.DeleteAsync(owner, persona.Id));
        Assert.Equal(PersonaIds.BuiltInMira, await Store.GetDefaultAsync(owner));
        Assert.Null(await Store.GetAsync(owner, persona.Id));
        Assert.Empty(await Store.ListVersionsAsync(owner, persona.Id));
        Assert.False(await Store.DeleteAsync(owner, persona.Id));
    }

    [Fact]
    public async Task TurnResolutionUsesThePinOrFallsBackToBuiltIn()
    {
        var alice = await CreateOwnerAsync();
        var bob = await CreateOwnerAsync();
        var mine = await Store.CreateAsync(alice, "Jack", null, "body");
        await Store.UpdateAsync(alice, mine.Id, "Jack", null, "body 2");
        var theirs = await Store.CreateAsync(bob, "Theirs", null, "secret style");

        var active = await Store.ResolveForTurnAsync(alice, mine.Id);
        Assert.False(active.DeletedPin);
        Assert.Equal((mine.Id, 2, "body 2"), (active.Persona.Id, active.Persona.ActiveVersion, active.Persona.Markdown));
        Assert.False((await Store.ResolveForTurnAsync(alice, PersonaIds.BuiltInMira)).DeletedPin);

        foreach (var pin in new[] { theirs.Id, Guid.NewGuid() })
        {
            var fallback = await Store.ResolveForTurnAsync(alice, pin);
            Assert.True(fallback.DeletedPin);
            Assert.Equal(PersonaIds.BuiltInMira, fallback.Persona.Id);
            Assert.Equal(BuiltInPersona.Markdown, fallback.Persona.Markdown);
        }

        await Store.DeleteAsync(alice, mine.Id);
        Assert.True((await Store.ResolveForTurnAsync(alice, mine.Id)).DeletedPin);
    }

    [Fact]
    public async Task AnotherOwnerSeesAndChangesNothing()
    {
        var alice = await CreateOwnerAsync();
        var bob = await CreateOwnerAsync();
        var persona = await Store.CreateAsync(alice, "Private", "desc", "private body");

        Assert.Null(await Store.GetAsync(bob, persona.Id));
        Assert.Empty(await Store.ListVersionsAsync(bob, persona.Id));
        Assert.Null(await Store.GetVersionAsync(bob, persona.Id, 1));
        Assert.DoesNotContain(await Store.ListAsync(bob), p => p.Id == persona.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.UpdateAsync(bob, persona.Id, "hijack", null, "x"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.RevertAsync(bob, persona.Id, 1));
        Assert.False(await Store.DeleteAsync(bob, persona.Id));
        Assert.Equal("private body", (await Store.GetAsync(alice, persona.Id))!.Markdown);
    }

    [Fact]
    public async Task ControlCharactersAreStrippedAndInvalidInputRejected()
    {
        var owner = await CreateOwnerAsync();
        var created = await Store.CreateAsync(owner, "Ja\0ck\u0007", "de\u001Bsc", "line one\n\tline two\0");
        Assert.Equal(("Jack", "desc", "line one\n\tline two"), (created.Name, created.Description, created.Markdown));

        await Assert.ThrowsAsync<ArgumentException>(() => Store.CreateAsync(owner, "  \0 ", null, "body"));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.CreateAsync(owner, new string('n', 81), null, "body"));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.CreateAsync(owner, "name", null, new string('b', 8001)));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ListAsync(Guid.Empty));
    }
}

public sealed class InMemoryPersonaStoreTests : PersonaStoreContractTests
{
    protected override Task<IPersonaStore> CreateStoreAsync() => Task.FromResult<IPersonaStore>(new InMemoryPersonaStore(Time));

    protected override Task<Guid> CreateOwnerAsync() => Task.FromResult(Guid.NewGuid());
}

public sealed class SqlitePersonaStoreTests : PersonaStoreContractTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mira-tests", Guid.NewGuid().ToString("N"));
    private MiraOptions _options = null!;
    private MiraDatabase _database = null!;
    private int _owners;

    private SqlitePersonaStore Sqlite => (SqlitePersonaStore)Store;

    protected override async Task<IPersonaStore> CreateStoreAsync()
    {
        _options = new MiraOptions { DatabasePath = Path.Combine(_directory, "mira.db") };
        _database = new MiraDatabase(_options);
        await _database.MigrateAsync();
        return new SqlitePersonaStore(_database, Time);
    }

    protected override async Task<Guid> CreateOwnerAsync() =>
        (await new SqliteUserAccountService(_database, _options, Time).CreateAsync($"owner{Interlocked.Increment(ref _owners)}", "synthetic fixture password")).Id;

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
    public async Task FreshDatabaseHasMigrationFiveSeededAndForeignKeysOn()
    {
        Assert.Equal(5, MiraDatabase.LatestVersion);
        await _database.MigrateAsync(); // idempotent: no second seed row
        await using var connection = await _database.OpenAsync();
        Assert.Equal(5, await MiraDatabase.GetVersionAsync(connection));
        Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Personas WHERE OwnerUserId IS NULL;"));
        Assert.Equal(BuiltInPersona.Markdown, await ScalarAsync(connection,
            "SELECT Markdown FROM PersonaVersions WHERE PersonaId = '00000000-0000-0000-0000-000000000001' AND Version = 1;"));
    }

    [Fact]
    public async Task V4DatabaseUpgradesWithConversationsPinnedToBuiltIn()
    {
        var options = new MiraOptions { DatabasePath = Path.Combine(_directory, "m3.db") };
        var database = new MiraDatabase(options);
        await database.MigrateAsync(4);
        var user = await new SqliteUserAccountService(database, options, Time).CreateAsync("m3-owner", "an m3 era fixture password");
        var conversationId = Guid.NewGuid();
        await using (var connection = await database.OpenAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Conversations (Id, OwnerUserId, Title, CreatedAt, UpdatedAt) VALUES ($id, $owner, 'old chat', $now, $now);
                INSERT INTO Messages (Id, ConversationId, Role, Content, Status, CreatedAt, ModelId, RequestId)
                VALUES ($messageId, $id, 'User', 'synthetic', 'Completed', $now, NULL, 'r1');
                """;
            insert.Parameters.AddWithValue("$id", conversationId.ToString());
            insert.Parameters.AddWithValue("$owner", user.Id.ToString());
            insert.Parameters.AddWithValue("$messageId", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("$now", "2026-11-17T09:00:00.0000000Z");
            await insert.ExecuteNonQueryAsync();
        }

        await database.MigrateAsync();
        await database.MigrateAsync();

        var snapshot = await new SqliteConversationStore(database, Time).GetAsync(user.Id, conversationId);
        Assert.Equal(PersonaIds.BuiltInMira, snapshot!.PersonaId);
        Assert.Equal("old chat", snapshot.Title);
        Assert.Single(snapshot.Messages);
        Assert.Equal(PersonaIds.BuiltInMira, await new SqlitePersonaStore(database, Time).GetDefaultAsync(user.Id));
    }

    [Fact]
    public async Task DeletingAPinnedPersonaKeepsPinsAndMessages()
    {
        var owner = await CreateOwnerAsync();
        var conversations = new SqliteConversationStore(_database, Time);
        var persona = await Store.CreateAsync(owner, "Jack", null, "body");
        await Store.SetDefaultAsync(owner, persona.Id);
        var conversation = await conversations.CreateAsync(owner, "chat", persona.Id);
        await conversations.AppendUserMessageAsync(owner, conversation.Id, "hello", "r1");

        Assert.True(await Store.DeleteAsync(owner, persona.Id));

        var snapshot = await conversations.GetAsync(owner, conversation.Id);
        Assert.Equal(persona.Id, snapshot!.PersonaId);
        Assert.Single(snapshot.Messages);
        Assert.Equal(PersonaIds.BuiltInMira, await Store.GetDefaultAsync(owner));
        var resolved = await Store.ResolveForTurnAsync(owner, snapshot.PersonaId);
        Assert.True(resolved.DeletedPin);
        Assert.Equal(persona.Id, (await conversations.GetAsync(owner, conversation.Id))!.PersonaId);
    }

    [Fact]
    public async Task ConversationPersonaChecksBothOwnershipsInOneStep()
    {
        var alice = await CreateOwnerAsync();
        var bob = await CreateOwnerAsync();
        var conversations = new SqliteConversationStore(_database, Time);
        var aliceChat = await conversations.CreateAsync(alice, null);
        var bobChat = await conversations.CreateAsync(bob, null);
        var alicePersona = await Store.CreateAsync(alice, "Jack", null, "body");
        var bobPersona = await Store.CreateAsync(bob, "Theirs", null, "body");

        Assert.Equal(ConversationPersonaResult.ConversationNotFound, await Sqlite.SetConversationPersonaAsync(alice, bobChat.Id, alicePersona.Id));
        Assert.Equal(ConversationPersonaResult.PersonaNotFound, await Sqlite.SetConversationPersonaAsync(alice, aliceChat.Id, bobPersona.Id));
        Assert.Equal(ConversationPersonaResult.PersonaNotFound, await Sqlite.SetConversationPersonaAsync(alice, aliceChat.Id, Guid.NewGuid()));
        Assert.Equal(PersonaIds.BuiltInMira, (await conversations.GetAsync(alice, aliceChat.Id))!.PersonaId);
        Assert.Equal(PersonaIds.BuiltInMira, (await conversations.GetAsync(bob, bobChat.Id))!.PersonaId);

        Assert.Equal(ConversationPersonaResult.Updated, await Sqlite.SetConversationPersonaAsync(alice, aliceChat.Id, alicePersona.Id));
        Assert.Equal(alicePersona.Id, (await conversations.GetAsync(alice, aliceChat.Id))!.PersonaId);
        Assert.Equal(ConversationPersonaResult.Updated, await Sqlite.SetConversationPersonaAsync(alice, aliceChat.Id, PersonaIds.BuiltInMira));
    }

    [Fact]
    public async Task ParallelSavesGetDistinctVersionsOrAConflictNeverAnotherError()
    {
        var owner = await CreateOwnerAsync();
        var persona = await Store.CreateAsync(owner, "Jack", null, "body");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            try
            {
                return (await Store.UpdateAsync(owner, persona.Id, $"Jack {i}", null, $"body {i}")).ActiveVersion;
            }
            catch (PersonaConflictException)
            {
                return 0;
            }
        }));

        var saved = outcomes.Where(v => v > 0).ToArray();
        Assert.Equal(saved.Length, saved.Distinct().Count());
        Assert.NotEmpty(saved);
        Assert.Equal(saved.Max(), (await Store.GetAsync(owner, persona.Id))!.ActiveVersion);
        Assert.True((await Store.ListVersionsAsync(owner, persona.Id)).Count <= PersonaLimits.MaxVersionsPerPersona);
    }

    [Fact]
    public async Task ConcurrentCreatesNeverExceedTheCap()
    {
        var owner = await CreateOwnerAsync();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 30).Select(async i =>
        {
            try
            {
                await Store.CreateAsync(owner, $"p{i}", null, "body");
                return true;
            }
            catch (PersonaConflictException)
            {
                return false;
            }
        }));

        Assert.Equal(PersonaLimits.MaxUserPersonas, outcomes.Count(ok => ok));
        await using var connection = await _database.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Personas WHERE OwnerUserId = $owner;";
        count.Parameters.AddWithValue("$owner", owner.ToString());
        Assert.Equal((long)PersonaLimits.MaxUserPersonas, await count.ExecuteScalarAsync());
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
