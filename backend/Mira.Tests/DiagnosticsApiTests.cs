using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mira.Api.Diagnostics;
using Mira.Core;
using Mira.Infrastructure;
using Mira.Infrastructure.Pipeline;

namespace Mira.Tests;

public sealed class DiagnosticsApiTests
{
    // Synthetic text; it must never appear in stored or served pipeline events.
    private const string DistinctMessage = "synthetic distinctive message zebra-quartz-41";

    [Fact]
    public async Task ATurnRecordsCorrelatedRedactedEvents()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");

        using var response = await client.SendAsync(ClientExtensions.StreamRequest(DistinctMessage));
        var sse = await Sse.ReadAllAsync(response);
        var requestId = sse[0].Data.GetProperty("requestId").GetString()!;
        var conversationId = sse[0].Data.GetProperty("conversationId").GetString();

        var raw = await client.GetStringAsync($"/api/diagnostics/{requestId}");
        Assert.DoesNotContain(DistinctMessage, raw);
        Assert.DoesNotContain(MockChatProvider.ResponseFor(DistinctMessage), raw);
        var trace = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(requestId, trace.GetProperty("requestId").GetString());
        Assert.Equal(conversationId, trace.GetProperty("conversationId").GetString());

        var events = trace.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["request.started", "response.first_token", "response.completed"], events.Select(e => e.GetProperty("eventType").GetString()));
        var started = events[0].GetProperty("data");
        Assert.Equal("mock-chat", started.GetProperty("modelId").GetString());
        Assert.Equal(DistinctMessage.Length, started.GetProperty("inputChars").GetInt32());
        Assert.Equal(1, started.GetProperty("promptTurns").GetInt32());
        Assert.True(events[1].GetProperty("data").GetProperty("firstTokenMs").GetInt64() >= 0);
        var done = events[2].GetProperty("data");
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal(MockChatProvider.ResponseFor(DistinctMessage).Length, done.GetProperty("outputChars").GetInt32());
        Assert.True(done.GetProperty("deltaCount").GetInt32() > 1);
        Assert.True(done.GetProperty("totalMs").GetInt64() >= done.GetProperty("firstTokenMs").GetInt64());
        Assert.All(events, e => Assert.False(e.TryGetProperty("ownerUserId", out _)));

        // Also check what is stored, not just what is served.
        var owner = (await client.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("id").GetGuid();
        var stored = await factory.Services.GetRequiredService<IPipelineEventStore>().GetByRequestAsync(owner, requestId);
        Assert.Equal(3, stored.Count);
        Assert.DoesNotContain(DistinctMessage, JsonSerializer.Serialize(stored));
    }

    [Fact]
    public async Task FailedAndCancelledTurnsEndWithExactlyOneTerminalEvent()
    {
        var provider = new BlockingChatProvider();
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");

        using (var failed = await client.SendAsync(ClientExtensions.StreamRequest("fail " + MockChatProvider.ErrorTrigger)))
        {
            var rid = (await Sse.ReadAllAsync(failed))[0].Data.GetProperty("requestId").GetString();
            var events = (await client.GetFromJsonAsync<JsonElement>($"/api/diagnostics/{rid}")).GetProperty("events").EnumerateArray().ToList();
            var last = events[^1];
            Assert.Equal("response.failed", last.GetProperty("eventType").GetString());
            Assert.Equal("provider_error", last.GetProperty("data").GetProperty("errorCode").GetString());
            Assert.Equal("failed", last.GetProperty("data").GetProperty("status").GetString());
            Assert.Single(events, e => IsTerminal(e.GetProperty("eventType").GetString()!));
        }

        using var blocking = new MiraApiFactory(services: s => s.AddSingleton<IChatProvider>(provider));
        using var other = await blocking.LoggedInClientAsync("owner");
        using var response = await other.SendAsync(ClientExtensions.StreamRequest("stop me"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var delta = await Sse.ReadUntilAsync(reader, "response.delta");
        await other.PostAsJsonAsync("/api/chat/cancel", new { });
        await Sse.ReadUntilAsync(reader, "response.cancelled");

        var trace = await other.GetFromJsonAsync<JsonElement>($"/api/diagnostics/{delta.Data.GetProperty("requestId").GetString()}");
        var cancelled = trace.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal("response.cancelled", cancelled[^1].GetProperty("eventType").GetString());
        Assert.Equal("cancelled", cancelled[^1].GetProperty("data").GetProperty("errorCode").GetString());
        Assert.Equal("interrupted", cancelled[^1].GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("blocking-test", cancelled[0].GetProperty("data").GetProperty("modelId").GetString());
        Assert.Single(cancelled, e => IsTerminal(e.GetProperty("eventType").GetString()!));
    }

    [Fact]
    public async Task RequestListIsOwnerScopedAndBounded()
    {
        using var factory = new MiraApiFactory();
        using var alice = await factory.LoggedInClientAsync("alice");
        using var bob = await factory.LoggedInClientAsync("bob");
        var conversation = await alice.CreateConversationAsync();
        string? rid;
        using (var r = await alice.SendAsync(ClientExtensions.StreamRequest("one", conversation)))
        {
            rid = (await Sse.ReadAllAsync(r))[0].Data.GetProperty("requestId").GetString();
        }

        var list = await alice.GetFromJsonAsync<JsonElement>($"/api/diagnostics?conversationId={conversation}");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(rid, item.GetProperty("requestId").GetString());
        Assert.Equal("response.completed", item.GetProperty("lastEventType").GetString());
        Assert.Single((await alice.GetFromJsonAsync<JsonElement>("/api/diagnostics")).GetProperty("items").EnumerateArray());

        Assert.Empty((await bob.GetFromJsonAsync<JsonElement>("/api/diagnostics")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/diagnostics?conversationId={conversation}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/diagnostics/{rid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/diagnostics/not-a-request-id")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/diagnostics/{rid!.ToUpperInvariant()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/diagnostics?take=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/diagnostics?take=51")).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/diagnostics/{rid}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/diagnostics")).StatusCode);
    }

    [Fact]
    public async Task RetentionPurgesEventsOlderThanThirtyDaysAtStartAndEveryDay()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryPipelineEventStore();
        var owner = Guid.NewGuid();
        await store.AppendAsync(Event(owner, "old", time.GetUtcNow() - TimeSpan.FromDays(31)));
        await store.AppendAsync(Event(owner, "recent", time.GetUtcNow() - TimeSpan.FromDays(29)));
        using var service = new PipelineRetentionService(store, time, NullLogger<PipelineRetentionService>.Instance);

        await service.StartAsync(CancellationToken.None); // first purge runs at startup
        await WaitUntilAsync(async () => (await store.GetByRequestAsync(owner, "old")).Count == 0);
        Assert.Single(await store.GetByRequestAsync(owner, "recent"));

        time.Advance(TimeSpan.FromDays(1.5)); // "recent" is now 30.5 days old; the daily purge removes it
        await WaitUntilAsync(async () => (await store.GetByRequestAsync(owner, "recent")).Count == 0);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void TheApiHostRunsTheRetentionService()
    {
        using var factory = new MiraApiFactory();
        Assert.Contains(factory.Services.GetServices<IHostedService>(), s => s is PipelineRetentionService);
    }

    private static bool IsTerminal(string eventType) =>
        eventType is PipelineEventTypes.Completed or PipelineEventTypes.Cancelled or PipelineEventTypes.ClientAborted
            or PipelineEventTypes.TimedOut or PipelineEventTypes.Failed;

    private static PipelineEvent Event(Guid owner, string requestId, DateTimeOffset createdAt) =>
        new(Guid.NewGuid(), owner, requestId, null, PipelineEventTypes.RequestStarted, 0, new PipelineEventData(), createdAt);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(20);
        }
    }

    // Emits one delta, then waits until cancelled.
    private sealed class BlockingChatProvider : IChatProvider
    {
        public string ModelId => "blocking-test";

        public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "partial ";
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield return "never";
        }
    }
}
