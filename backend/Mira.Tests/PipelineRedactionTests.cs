using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mira.Core;
using Mira.Infrastructure.Pipeline;

namespace Mira.Tests;

// All "secret" values below are synthetic and only shaped like the real thing.
public sealed class PipelineRedactionTests
{
    private const string MessageText = "Synthetic private message: meet me at the usual place at nine";
    private const string LongPassword = "synthetic-fixture-password-that-is-over-35-chars";
    private const string SessionCookie = "mira_session=CfDJ8SyntheticSessionCookieValue0123456789abcdef";
    private const string CsrfToken = "CfDJ8SyntheticCsrfTokenValue_abcdefghijklmnopqrstuvwxyz0123";
    private const string ApiKey = "sk-proj-SyntheticKeyValue0123456789abcdefghijklmnop";

    public static TheoryData<string> Secrets => new() { MessageText, LongPassword, SessionCookie, CsrfToken, ApiKey };

    private static readonly string[] ConfiguredModels = ["mock-chat", "provider/model:2026.11_a"];

    private readonly PipelineRedactor _redactor = new(ConfiguredModels);

    [Theory]
    [MemberData(nameof(Secrets))]
    public void NoTextSurvivesInErrorCode(string secret)
    {
        foreach (var eventType in PipelineEventTypes.All)
        {
            Assert.Null(_redactor.Redact(eventType, new PipelineEventData(ErrorCode: secret)).ErrorCode);
        }
    }

    // ModelId is an allowlist of configured model IDs: identifier-shaped secrets (a hyphenated password, a CSRF token,
    // an sk-... key) are replaced by the fixed placeholder like any other unlisted value.
    [Theory]
    [MemberData(nameof(Secrets))]
    [InlineData("mock\nmodel")]
    [InlineData("mock")]
    [InlineData("MOCK-CHAT")]
    [InlineData("mock-chat ")]
    [InlineData("")]
    public void NoSecretOrUnlistedValueSurvivesInModelId(string value)
    {
        foreach (var eventType in PipelineEventTypes.All)
        {
            Assert.Equal(PipelineRedactor.UnlistedModelId, _redactor.Redact(eventType, new PipelineEventData(ModelId: value)).ModelId);
        }
    }

    [Theory]
    [InlineData("mock-chat")]
    [InlineData("provider/model:2026.11_a")]
    public void ConfiguredModelIdsAreKept(string modelId)
    {
        Assert.Equal(modelId, _redactor.Redact(PipelineEventTypes.RequestStarted, new PipelineEventData(ModelId: modelId)).ModelId);
    }

    [Fact]
    public void MissingModelIdStaysMissing()
    {
        Assert.Null(_redactor.Redact(PipelineEventTypes.RequestStarted, new PipelineEventData()).ModelId);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData(PipelineRedactor.UnlistedModelId)]
    public void MalformedConfiguredModelIdIsAStartupError(string configured)
    {
        Assert.Throws<ArgumentException>(() => new PipelineRedactor([configured]));
    }

    [Fact]
    public void KnownErrorCodesAndNumbersAreKeptNegativesDropped()
    {
        foreach (var code in PipelineErrorCodes.All)
        {
            Assert.Equal(code, _redactor.Redact(PipelineEventTypes.Failed, new PipelineEventData(ErrorCode: code)).ErrorCode);
        }

        var kept = new PipelineEventData(null, MessageStatus.Interrupted, 1, 2, 3, 4, 5, 6, null);
        Assert.Equal(kept, _redactor.Redact(PipelineEventTypes.Cancelled, kept));
        var negative = new PipelineEventData(null, (MessageStatus)99, -1, -2, -3, -4, -5, -6, null);
        Assert.Equal(new PipelineEventData(), _redactor.Redact(PipelineEventTypes.Completed, negative));
        Assert.Equal(new PipelineEventData(InputChars: 0, TotalMs: 0), _redactor.Redact(PipelineEventTypes.Completed, new PipelineEventData(InputChars: 0, TotalMs: 0)));
    }

    [Theory]
    [InlineData("request.unknown")]
    [InlineData("REQUEST.STARTED")]
    [InlineData("")]
    public void UnknownEventTypeIsRejected(string eventType)
    {
        Assert.Throws<ArgumentException>(() => _redactor.Redact(eventType, new PipelineEventData()));
    }

    [Fact]
    public void PayloadRecordHasOnlyTheAllowlistedFields()
    {
        // Adding a field to PipelineEventData must be a deliberate, reviewed change: update this list and the redactor together.
        var expected = new Dictionary<string, Type>
        {
            ["ModelId"] = typeof(string),
            ["Status"] = typeof(MessageStatus?),
            ["InputChars"] = typeof(int?),
            ["OutputChars"] = typeof(int?),
            ["PromptTurns"] = typeof(int?),
            ["DeltaCount"] = typeof(int?),
            ["FirstTokenMs"] = typeof(long?),
            ["TotalMs"] = typeof(long?),
            ["ErrorCode"] = typeof(string),
            ["PersonaId"] = typeof(Guid?),
            ["PersonaVersion"] = typeof(int?)
        };
        var actual = typeof(PipelineEventData)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .ToDictionary(p => p.Name, p => p.PropertyType);
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
        Assert.Empty(typeof(PipelineEventData).GetFields(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void PersonaIdAndVersionAreKeptAndEmptyOrNonPositiveDropped()
    {
        var personaId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var kept = new PipelineEventData(PersonaId: personaId, PersonaVersion: 3);
        Assert.Equal(kept, _redactor.Redact(PipelineEventTypes.RequestStarted, kept));
        var dropped = _redactor.Redact(PipelineEventTypes.Completed, new PipelineEventData(PersonaId: Guid.Empty, PersonaVersion: 0));
        Assert.Null(dropped.PersonaId);
        Assert.Null(dropped.PersonaVersion);
        Assert.Null(_redactor.Redact(PipelineEventTypes.Completed, new PipelineEventData(PersonaVersion: -1)).PersonaVersion);
        Assert.DoesNotContain(typeof(PipelineEventData).GetProperties().Select(p => p.Name), name =>
            name is "Name" or "Description" or "Markdown" or "Body" or "PersonaName");
    }

    [Fact]
    public async Task RecorderRedactsStampsAndAppends()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.Zero));
        var store = new InMemoryPipelineEventStore();
        var (recorder, _) = Recorder(store, time);
        var owner = Guid.NewGuid();
        var conversation = Guid.NewGuid();

        await recorder.RecordAsync(owner, "r1", conversation, PipelineEventTypes.RequestStarted, 0,
            new PipelineEventData(ModelId: MessageText, InputChars: MessageText.Length, PromptTurns: 1));
        time.Advance(TimeSpan.FromMilliseconds(15));
        await recorder.RecordAsync(owner, "r1", conversation, PipelineEventTypes.FirstToken, 15, new PipelineEventData(FirstTokenMs: 15));

        var events = await store.GetByRequestAsync(owner, "r1");
        Assert.Equal(2, events.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.Zero), events[0].CreatedAt);
        Assert.Equal(time.GetUtcNow(), events[1].CreatedAt);
        Assert.Equal(PipelineRedactor.UnlistedModelId, events[0].Data.ModelId);
        Assert.Equal(MessageText.Length, events[0].Data.InputChars);
        Assert.NotEqual(Guid.Empty, events[0].Id);
        Assert.NotEqual(events[0].Id, events[1].Id);
        Assert.Equal(conversation, events[1].ConversationId);
        Assert.Equal(15, events[1].ElapsedMs);
        Assert.Empty(await store.GetByRequestAsync(Guid.NewGuid(), "r1"));
    }

    [Fact]
    public async Task RecorderStillWritesWhenTheTurnIsCancelled()
    {
        var store = new InMemoryPipelineEventStore();
        var (recorder, _) = Recorder(store, new FakeTimeProvider());
        var owner = Guid.NewGuid();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await recorder.RecordAsync(owner, "r1", null, PipelineEventTypes.Cancelled, 9,
            new PipelineEventData(Status: MessageStatus.Interrupted, ErrorCode: PipelineErrorCodes.Cancelled), cancelled.Token);

        Assert.Equal(PipelineEventTypes.Cancelled, Assert.Single(await store.GetByRequestAsync(owner, "r1")).EventType);
    }

    [Fact]
    public async Task RecorderSwallowsFailuresAndLogsNoPayload()
    {
        var (recorder, logs) = Recorder(new ThrowingStore(), new FakeTimeProvider());

        await recorder.RecordAsync(Guid.NewGuid(), "r1", null, PipelineEventTypes.Failed, 1,
            new PipelineEventData(ModelId: "mock", ErrorCode: PipelineErrorCodes.ProviderError));
        await recorder.RecordAsync(Guid.NewGuid(), "r2", null, "not.a.type", 1, new PipelineEventData(ModelId: ApiKey));

        var entries = logs.ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Contains(nameof(InvalidOperationException), entries[0]);
        Assert.Contains(nameof(ArgumentException), entries[1]);
        foreach (var entry in entries)
        {
            Assert.DoesNotContain(ThrowingStore.SecretMessage, entry);
            Assert.DoesNotContain(ApiKey, entry);
            Assert.DoesNotContain("mock", entry);
            Assert.DoesNotContain("r1", entry);
        }
    }

    private static (PipelineRecorder Recorder, ConcurrentQueue<string> Logs) Recorder(IPipelineEventStore store, TimeProvider time)
    {
        var logs = new ConcurrentQueue<string>();
        var factory = LoggerFactory.Create(builder => builder.AddProvider(new CollectingLoggerProvider(logs)));
        return (new PipelineRecorder(store, new PipelineRedactor(["mock"]), time, factory.CreateLogger<PipelineRecorder>()), logs);
    }

    private sealed class ThrowingStore : IPipelineEventStore
    {
        public const string SecretMessage = "store failure that quotes " + MessageText;

        public Task AppendAsync(PipelineEvent pipelineEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(SecretMessage);

        public Task<IReadOnlyList<PipelineEvent>> GetByRequestAsync(Guid ownerUserId, string requestId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(SecretMessage);

        public Task<IReadOnlyList<PipelineRequestSummary>> ListRecentRequestsAsync(Guid ownerUserId, Guid? conversationId, int take, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(SecretMessage);

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(SecretMessage);
    }
}
