using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Mira.Api.Auth;
using Mira.Core;
using Mira.Infrastructure;
using Mira.Infrastructure.Pipeline;
using Mira.Infrastructure.Sqlite;

namespace Mira.Tests;

// IConversationStore contract, run against every implementation. Fixture text is synthetic.
public abstract class ConversationStoreContractTests : IAsyncLifetime
{
    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    protected IConversationStore Store { get; private set; } = null!;

    public virtual async Task InitializeAsync() => Store = await CreateStoreAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    protected abstract Task<IConversationStore> CreateStoreAsync();

    // A valid owner (the SQLite store needs a Users row for the foreign key).
    protected abstract Task<Guid> CreateOwnerAsync();

    [Fact]
    public async Task CreateGetAndMessageOrderIsStable()
    {
        var owner = await CreateOwnerAsync();
        var created = await Store.CreateAsync(owner, "  Synthetic title  ");
        Assert.Equal("Synthetic title", created.Title);

        // Same timestamp on purpose: insertion order must still hold.
        await Store.AppendUserMessageAsync(owner, created.Id, "first", "r1");
        var reply = await Store.BeginAssistantMessageAsync(owner, created.Id, "mock-model", "r1");
        await Store.AppendUserMessageAsync(owner, created.Id, "third", "r2");
        await Store.FinishAssistantMessageAsync(owner, created.Id, reply.Id, "second", MessageStatus.Completed);

        var snapshot = await Store.GetAsync(owner, created.Id);
        Assert.NotNull(snapshot);
        Assert.Equal(owner, snapshot.OwnerUserId);
        Assert.Equal(["first", "second", "third"], snapshot.Messages.Select(m => m.Content));
        Assert.Equal(MessageStatus.Completed, snapshot.Messages[1].Status);
        Assert.Equal("mock-model", snapshot.Messages[1].ModelId);
        Assert.Equal("r1", snapshot.Messages[1].RequestId);
        Assert.Null(snapshot.Messages[0].ModelId);
        Assert.Equal(reply.Id, snapshot.Messages[1].Id);
    }

    [Fact]
    public async Task ListIsNewestActivityFirstAndPaged()
    {
        var owner = await CreateOwnerAsync();
        var a = await Store.CreateAsync(owner, "a");
        Time.Advance(TimeSpan.FromSeconds(1));
        var b = await Store.CreateAsync(owner, "b");
        Time.Advance(TimeSpan.FromSeconds(1));
        var c = await Store.CreateAsync(owner, "c");
        Time.Advance(TimeSpan.FromSeconds(1));
        await Store.AppendUserMessageAsync(owner, a.Id, "bump", "r1");

        var all = await Store.ListAsync(owner, 0, ConversationLimits.MaxPageSize);
        Assert.Equal([a.Id, c.Id, b.Id], all.Select(s => s.Id));
        Assert.Equal([c.Id], (await Store.ListAsync(owner, 1, 1)).Select(s => s.Id));
        Assert.Empty(await Store.ListAsync(owner, 3, 10));

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => Store.ListAsync(owner, -1, 10));
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => Store.ListAsync(owner, 0, 0));
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => Store.ListAsync(owner, 0, ConversationLimits.MaxPageSize + 1));
    }

    [Fact]
    public async Task MostRecentOrCreateReusesTheLatestConversation()
    {
        var owner = await CreateOwnerAsync();
        var first = await Store.GetMostRecentOrCreateAsync(owner);
        Assert.Null(first.Title);
        Assert.Empty(first.Messages);
        Assert.Equal(first.Id, (await Store.GetMostRecentOrCreateAsync(owner)).Id);

        Time.Advance(TimeSpan.FromSeconds(1));
        var newer = await Store.CreateAsync(owner, null);
        Assert.Equal(newer.Id, (await Store.GetMostRecentOrCreateAsync(owner)).Id);
        Assert.Equal(2, (await Store.ListAsync(owner, 0, 10)).Count);
    }

    [Fact]
    public async Task RenameFollowsTitleRulesAndBumpsUpdatedAt()
    {
        var owner = await CreateOwnerAsync();
        var created = await Store.CreateAsync(owner, "old");
        Time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await Store.RenameAsync(owner, created.Id, "  new  "));
        var renamed = await Store.GetAsync(owner, created.Id);
        Assert.Equal("new", renamed!.Title);
        Assert.Equal(Time.GetUtcNow(), renamed.UpdatedAt);
        Assert.Equal(created.CreatedAt, renamed.CreatedAt);

        Assert.True(await Store.RenameAsync(owner, created.Id, "   "));
        Assert.Null((await Store.GetAsync(owner, created.Id))!.Title);

        var maxTitle = new string('t', ConversationLimits.MaxTitleLength);
        Assert.True(await Store.RenameAsync(owner, created.Id, maxTitle));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.RenameAsync(owner, created.Id, maxTitle + "t"));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.CreateAsync(owner, maxTitle + "t"));
        Assert.False(await Store.RenameAsync(owner, Guid.NewGuid(), "missing"));
    }

    [Fact]
    public async Task DeleteRemovesOnlyThatConversation()
    {
        var owner = await CreateOwnerAsync();
        var doomed = await Store.CreateAsync(owner, "doomed");
        var kept = await Store.CreateAsync(owner, "kept");
        await Store.AppendUserMessageAsync(owner, doomed.Id, "x", "r1");
        await Store.AppendUserMessageAsync(owner, kept.Id, "y", "r2");

        Assert.True(await Store.DeleteAsync(owner, doomed.Id));
        Assert.Null(await Store.GetAsync(owner, doomed.Id));
        Assert.Empty(await Store.GetPromptTurnsAsync(owner, doomed.Id));
        Assert.False(await Store.DeleteAsync(owner, doomed.Id));
        Assert.Single((await Store.GetAsync(owner, kept.Id))!.Messages);
        Assert.Equal([kept.Id], (await Store.ListAsync(owner, 0, 10)).Select(s => s.Id));
    }

    [Fact]
    public async Task PromptTurnsHoldUserMessagesAndCompletedRepliesOnly()
    {
        var owner = await CreateOwnerAsync();
        var conversation = await Store.CreateAsync(owner, null);
        await Store.AppendUserMessageAsync(owner, conversation.Id, "one", "r1");
        var interrupted = await Store.BeginAssistantMessageAsync(owner, conversation.Id, "mock", "r1");
        await Store.FinishAssistantMessageAsync(owner, conversation.Id, interrupted.Id, "half", MessageStatus.Interrupted);
        await Store.AppendUserMessageAsync(owner, conversation.Id, "two", "r2");
        var failed = await Store.BeginAssistantMessageAsync(owner, conversation.Id, "mock", "r2");
        await Store.FinishAssistantMessageAsync(owner, conversation.Id, failed.Id, "", MessageStatus.Failed);
        await Store.AppendUserMessageAsync(owner, conversation.Id, "three", "r3");
        var completed = await Store.BeginAssistantMessageAsync(owner, conversation.Id, "mock", "r3");
        await Store.BeginAssistantMessageAsync(owner, conversation.Id, "mock", "r4"); // still streaming
        await Store.FinishAssistantMessageAsync(owner, conversation.Id, completed.Id, "done", MessageStatus.Completed);

        var turns = await Store.GetPromptTurnsAsync(owner, conversation.Id);
        Assert.Equal(
            [new ChatTurn(ChatRole.User, "one"), new ChatTurn(ChatRole.User, "two"), new ChatTurn(ChatRole.User, "three"), new ChatTurn(ChatRole.Assistant, "done")],
            turns);
    }

    [Fact]
    public async Task InvalidWritesAreRejected()
    {
        var owner = await CreateOwnerAsync();
        var conversation = await Store.CreateAsync(owner, null);
        var user = await Store.AppendUserMessageAsync(owner, conversation.Id, "hi", "r1");
        var reply = await Store.BeginAssistantMessageAsync(owner, conversation.Id, "mock", "r1");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.AppendUserMessageAsync(owner, conversation.Id, " ", "r2"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.AppendUserMessageAsync(owner, conversation.Id, "hi", ""));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Store.FinishAssistantMessageAsync(owner, conversation.Id, reply.Id, "x", MessageStatus.Streaming));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Store.FinishAssistantMessageAsync(owner, conversation.Id, user.Id, "x", MessageStatus.Completed));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Store.FinishAssistantMessageAsync(owner, conversation.Id, Guid.NewGuid(), "x", MessageStatus.Completed));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.AppendUserMessageAsync(owner, Guid.NewGuid(), "hi", "r3"));
    }

    [Fact]
    public async Task AnotherOwnerSeesNothingAndChangesNothing()
    {
        var alice = await CreateOwnerAsync();
        var bob = await CreateOwnerAsync();
        var conversation = await Store.CreateAsync(alice, "private");
        await Store.AppendUserMessageAsync(alice, conversation.Id, "hello", "r1");
        var reply = await Store.BeginAssistantMessageAsync(alice, conversation.Id, "mock", "r1");

        Assert.Empty(await Store.ListAsync(bob, 0, 10));
        Assert.Null(await Store.GetAsync(bob, conversation.Id));
        Assert.Empty(await Store.GetPromptTurnsAsync(bob, conversation.Id));
        Assert.False(await Store.RenameAsync(bob, conversation.Id, "hijack"));
        Assert.False(await Store.DeleteAsync(bob, conversation.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.AppendUserMessageAsync(bob, conversation.Id, "x", "r2"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.BeginAssistantMessageAsync(bob, conversation.Id, "mock", "r2"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Store.FinishAssistantMessageAsync(bob, conversation.Id, reply.Id, "hijack", MessageStatus.Completed));
        Assert.NotEqual(conversation.Id, (await Store.GetMostRecentOrCreateAsync(bob)).Id);

        var unchanged = await Store.GetAsync(alice, conversation.Id);
        Assert.Equal("private", unchanged!.Title);
        Assert.Equal(2, unchanged.Messages.Count);
        Assert.Equal(MessageStatus.Streaming, unchanged.Messages[1].Status);
    }

    [Fact]
    public async Task EmptyOwnerIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ListAsync(Guid.Empty, 0, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.CreateAsync(Guid.Empty, null));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetAsync(Guid.Empty, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetMostRecentOrCreateAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.RenameAsync(Guid.Empty, Guid.NewGuid(), null));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.DeleteAsync(Guid.Empty, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetPromptTurnsAsync(Guid.Empty, Guid.NewGuid()));
    }
}

public sealed class InMemoryConversationStoreTests : ConversationStoreContractTests
{
    protected override Task<IConversationStore> CreateStoreAsync() => Task.FromResult<IConversationStore>(new InMemoryConversationStore(Time));

    protected override Task<Guid> CreateOwnerAsync() => Task.FromResult(Guid.NewGuid());
}

public sealed class SqliteConversationStoreTests : ConversationStoreContractTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mira-tests", Guid.NewGuid().ToString("N"));
    private MiraOptions _options = null!;
    private MiraDatabase _database = null!;
    private int _owners;

    protected override async Task<IConversationStore> CreateStoreAsync()
    {
        _options = new MiraOptions { DatabasePath = Path.Combine(_directory, "mira.db") };
        _database = new MiraDatabase(_options);
        await _database.MigrateAsync();
        return new SqliteConversationStore(_database, Time);
    }

    protected override async Task<Guid> CreateOwnerAsync()
    {
        var accounts = new SqliteUserAccountService(_database, _options, Time);
        return (await accounts.CreateAsync($"owner{++_owners}", "synthetic fixture password")).Id;
    }

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
    public async Task FreshDatabaseIsAtVersionThreeAndMigrationsAreIdempotent()
    {
        Assert.Equal(3, MiraDatabase.LatestVersion);
        await _database.MigrateAsync();
        await _database.MigrateAsync();
        await using var connection = await _database.OpenAsync();
        Assert.Equal(3, await MiraDatabase.GetVersionAsync(connection));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Conversations', 'Messages', 'PipelineEvents');";
        Assert.Equal(3L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task M1DatabaseUpgradesAndKeepsUsersAndSessions()
    {
        var options = new MiraOptions { DatabasePath = Path.Combine(_directory, "m1.db") };
        var database = new MiraDatabase(options);
        await database.MigrateAsync(1);
        var accounts = new SqliteUserAccountService(database, options, Time);
        var user = await accounts.CreateAsync("m1-owner", "an m1 era fixture password");
        var sessions = new SqliteSessionStore(database, options, Time);
        var token = await sessions.CreateAsync(user.Id);
        await using (var connection = await database.OpenAsync())
        {
            Assert.Equal(1, await MiraDatabase.GetVersionAsync(connection));
        }

        await database.MigrateAsync();

        await using (var connection = await database.OpenAsync())
        {
            Assert.Equal(3, await MiraDatabase.GetVersionAsync(connection));
        }

        Assert.NotNull(await accounts.VerifyCredentialsAsync("m1-owner", "an m1 era fixture password"));
        Assert.Equal(user.Id, await sessions.ValidateAsync(token));
        var store = new SqliteConversationStore(database, Time);
        Assert.Empty((await store.GetMostRecentOrCreateAsync(user.Id)).Messages);
    }

    [Fact]
    public async Task StartupSweepIsSeparateFromMigrationAndMarksStreamingMessagesInterrupted()
    {
        var owner = await CreateOwnerAsync();
        var conversation = await Store.CreateAsync(owner, null);
        await Store.AppendUserMessageAsync(owner, conversation.Id, "question", "r1");
        await Store.BeginAssistantMessageAsync(owner, conversation.Id, "mock", "r1"); // process "crashes" mid-turn

        await _database.MigrateAsync(); // create-user path: migrating alone must not touch a live turn
        Assert.Equal(MessageStatus.Streaming, (await Store.GetAsync(owner, conversation.Id))!.Messages[^1].Status);

        Assert.Equal(1, await _database.InterruptStreamingMessagesAsync()); // API startup
        Assert.Equal(0, await _database.InterruptStreamingMessagesAsync());

        var snapshot = await Store.GetAsync(owner, conversation.Id);
        Assert.Equal([MessageStatus.Completed, MessageStatus.Interrupted], snapshot!.Messages.Select(m => m.Status));
        Assert.Equal([new ChatTurn(ChatRole.User, "question")], await Store.GetPromptTurnsAsync(owner, conversation.Id));
    }

    [Fact]
    public async Task DeleteAlsoRemovesTheConversationsPipelineEventsOnly()
    {
        var events = new SqlitePipelineEventStore(_database);
        var owner = await CreateOwnerAsync();
        var doomed = await Store.CreateAsync(owner, null);
        var kept = await Store.CreateAsync(owner, null);
        await events.AppendAsync(Event(owner, "r1", doomed.Id, Time.GetUtcNow()));
        await events.AppendAsync(Event(owner, "r2", kept.Id, Time.GetUtcNow()));
        await events.AppendAsync(Event(owner, "r3", null, Time.GetUtcNow()));

        Assert.True(await Store.DeleteAsync(owner, doomed.Id));

        Assert.Empty(await events.GetByRequestAsync(owner, "r1"));
        Assert.Single(await events.GetByRequestAsync(owner, "r2"));
        Assert.Single(await events.GetByRequestAsync(owner, "r3"));
        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Messages WHERE ConversationId = $id;";
        command.Parameters.AddWithValue("$id", doomed.Id.ToString());
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EventsForADeletedOrForeignConversationAreDropped()
    {
        var events = new SqlitePipelineEventStore(_database);
        var owner = await CreateOwnerAsync();
        var other = await CreateOwnerAsync();
        var conversation = await Store.CreateAsync(owner, null);
        Assert.True(await Store.DeleteAsync(owner, conversation.Id));
        var foreign = await Store.CreateAsync(other, null);

        await events.AppendAsync(Event(owner, "late", conversation.Id, Time.GetUtcNow()));  // turn still running after delete
        await events.AppendAsync(Event(owner, "foreign", foreign.Id, Time.GetUtcNow()));    // someone else's conversation
        await events.AppendAsync(Event(owner, "missing", Guid.NewGuid(), Time.GetUtcNow()));
        await events.AppendAsync(Event(owner, "none", null, Time.GetUtcNow()));

        Assert.Empty(await events.GetByRequestAsync(owner, "late"));
        Assert.Empty(await events.GetByRequestAsync(owner, "foreign"));
        Assert.Empty(await events.GetByRequestAsync(owner, "missing"));
        Assert.Single(await events.GetByRequestAsync(owner, "none"));
    }

    [Fact]
    public async Task PipelineEventsRoundTripOwnerScopedAndOrdered()
    {
        var events = new SqlitePipelineEventStore(_database);
        var alice = await CreateOwnerAsync();
        var bob = await CreateOwnerAsync();
        var conversation = await Store.CreateAsync(alice, null);
        var start = Time.GetUtcNow();
        var data = new PipelineEventData(ModelId: "mock", Status: MessageStatus.Completed, OutputChars: 12, DeltaCount: 3, TotalMs: 40);
        await events.AppendAsync(Event(alice, "r1", conversation.Id, start, PipelineEventTypes.RequestStarted));
        await events.AppendAsync(Event(alice, "r1", conversation.Id, start, PipelineEventTypes.FirstToken, new PipelineEventData(FirstTokenMs: 7)));
        await events.AppendAsync(Event(alice, "r1", conversation.Id, start.AddMilliseconds(40), PipelineEventTypes.Completed, data));
        await events.AppendAsync(Event(alice, "r2", null, start.AddSeconds(5)));

        var stored = await events.GetByRequestAsync(alice, "r1");
        Assert.Equal([PipelineEventTypes.RequestStarted, PipelineEventTypes.FirstToken, PipelineEventTypes.Completed], stored.Select(e => e.EventType));
        Assert.Equal(data, stored[2].Data);
        Assert.Equal(conversation.Id, stored[2].ConversationId);
        Assert.Equal(start.AddMilliseconds(40), stored[2].CreatedAt);
        Assert.Empty(await events.GetByRequestAsync(bob, "r1"));
        Assert.Empty(await events.GetByRequestAsync(alice, "missing"));

        var recent = await events.ListRecentRequestsAsync(alice, null, 10);
        Assert.Equal(["r2", "r1"], recent.Select(r => r.RequestId));
        Assert.Equal(new PipelineRequestSummary("r1", conversation.Id, start, PipelineEventTypes.Completed, 7, 40), recent[1]);
        Assert.Equal(["r1"], (await events.ListRecentRequestsAsync(alice, conversation.Id, 10)).Select(r => r.RequestId));
        Assert.Equal(["r2"], (await events.ListRecentRequestsAsync(alice, null, 1)).Select(r => r.RequestId));
        Assert.Empty(await events.ListRecentRequestsAsync(bob, null, 10));
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => events.ListRecentRequestsAsync(alice, null, 0));
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() =>
            events.ListRecentRequestsAsync(alice, null, PipelineEventRetention.MaxRequestsPerQuery + 1));
    }

    [Fact]
    public async Task PayloadColumnHoldsOnlyAllowlistedJson()
    {
        var events = new SqlitePipelineEventStore(_database);
        var owner = await CreateOwnerAsync();
        await events.AppendAsync(Event(owner, "r1", null, Time.GetUtcNow(), PipelineEventTypes.Failed,
            new PipelineEventData(Status: MessageStatus.Failed, ErrorCode: PipelineErrorCodes.ProviderError)));

        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RedactedPayload FROM PipelineEvents;";
        var json = (string)(await command.ExecuteScalarAsync())!;
        Assert.Equal("""{"Status":"Failed","ErrorCode":"provider_error"}""", json);
    }

    [Fact]
    public async Task PurgeRemovesOnlyEventsOlderThanTheCutoff()
    {
        var events = new SqlitePipelineEventStore(_database);
        var owner = await CreateOwnerAsync();
        var other = await CreateOwnerAsync();
        var now = Time.GetUtcNow();
        var cutoff = now - PipelineEventRetention.Period;
        await events.AppendAsync(Event(owner, "old", null, cutoff.AddTicks(-1)));
        await events.AppendAsync(Event(other, "old-other", null, cutoff.AddDays(-3)));
        await events.AppendAsync(Event(owner, "edge", null, cutoff));
        await events.AppendAsync(Event(owner, "new", null, now));

        Assert.Equal(2, await events.PurgeOlderThanAsync(cutoff));
        Assert.Empty(await events.GetByRequestAsync(owner, "old"));
        Assert.Empty(await events.GetByRequestAsync(other, "old-other"));
        Assert.Single(await events.GetByRequestAsync(owner, "edge"));
        Assert.Single(await events.GetByRequestAsync(owner, "new"));
        Assert.Equal(0, await events.PurgeOlderThanAsync(cutoff));
    }

    private static PipelineEvent Event(
        Guid owner, string requestId, Guid? conversationId, DateTimeOffset createdAt,
        string eventType = PipelineEventTypes.RequestStarted, PipelineEventData? data = null) =>
        new(Guid.NewGuid(), owner, requestId, conversationId, eventType, 0, data ?? new PipelineEventData(ModelId: "mock", InputChars: 5), createdAt);
}
