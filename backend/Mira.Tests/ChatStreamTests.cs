using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mira.Core;
using Mira.Infrastructure;

namespace Mira.Tests;

public sealed class ChatStreamTests
{
    private const string Password = "a perfectly ordinary passphrase of forty+ characters";

    [Fact]
    public async Task AuthenticatedUserReceivesOrderedDeltasAndACompletedTurn()
    {
        using var factory = new MiraApiFactory();
        using var client = await LoggedInClientAsync(factory, "owner");

        using var response = await client.SendAsync(ClientExtensions.StreamRequest("hello there"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = await Sse.ReadAllAsync(response);
        Assert.Equal("response.started", events[0].Event);
        Assert.Equal("response.completed", events[^1].Event);
        var deltas = events.Where(e => e.Event == "response.delta").ToList();
        Assert.True(deltas.Count > 1);
        Assert.Equal(MockChatProvider.ResponseFor("hello there"), string.Concat(deltas.Select(d => d.Data.GetProperty("data").GetProperty("text").GetString())));

        var requestIds = events.Select(e => e.Data.GetProperty("requestId").GetString()).Distinct().ToList();
        Assert.Single(requestIds);
        Assert.All(events, e =>
        {
            Assert.Equal(1, e.Data.GetProperty("v").GetInt32());
            Assert.Equal(e.Event, e.Data.GetProperty("type").GetString());
            Assert.True(e.Data.TryGetProperty("conversationId", out _));
            Assert.True(e.Data.TryGetProperty("timestamp", out _));
        });
        Assert.Equal("completed", events[^1].Data.GetProperty("data").GetProperty("status").GetString());

        var conversation = await client.GetFromJsonAsync<JsonElement>("/api/conversations/current");
        var messages = conversation.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hello there", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("completed", messages[1].GetProperty("status").GetString());
        Assert.Equal("mock-chat", messages[1].GetProperty("modelId").GetString());
        Assert.Equal(events[0].Data.GetProperty("conversationId").GetString(), conversation.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ClientAbortStopsTheProviderAndMarksTheTurnInterrupted()
    {
        var provider = new BlockingChatProvider();
        using var factory = new MiraApiFactory(services: s => s.AddSingleton<IChatProvider>(provider));
        using var client = await LoggedInClientAsync(factory, "owner");

        using (var abort = new CancellationTokenSource())
        {
            var response = await client.SendAsync(ClientExtensions.StreamRequest("long answer please"), HttpCompletionOption.ResponseHeadersRead, abort.Token);
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync(abort.Token));
            var delta = await Sse.ReadUntilAsync(reader, "response.delta");
            Assert.Equal("partial ", delta.Data.GetProperty("data").GetProperty("text").GetString());

            abort.Cancel();
            response.Dispose();
        }

        // The provider observes cancellation through HttpContext.RequestAborted.
        Assert.True(await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var assistant = await WaitForAssistantStatusAsync(client, "interrupted");
        Assert.Equal("partial ", assistant.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ExplicitCancelEmitsResponseCancelled()
    {
        var provider = new BlockingChatProvider();
        using var factory = new MiraApiFactory(services: s => s.AddSingleton<IChatProvider>(provider));
        using var client = await LoggedInClientAsync(factory, "owner");

        using var response = await client.SendAsync(ClientExtensions.StreamRequest("stop me"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await Sse.ReadUntilAsync(reader, "response.delta");

        var cancel = await client.PostAsJsonAsync("/api/chat/cancel", new { });
        Assert.True((await cancel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancelled").GetBoolean());

        var cancelled = await Sse.ReadUntilAsync(reader, "response.cancelled");
        Assert.Equal("interrupted", cancelled.Data.GetProperty("data").GetProperty("status").GetString());
        await WaitForAssistantStatusAsync(client, "interrupted");
    }

    [Fact]
    public async Task ProviderFailureEmitsResponseErrorAndKeepsPartialTextAsFailed()
    {
        using var factory = new MiraApiFactory();
        using var client = await LoggedInClientAsync(factory, "owner");

        using var response = await client.SendAsync(ClientExtensions.StreamRequest("please fail " + MockChatProvider.ErrorTrigger));
        var events = await Sse.ReadAllAsync(response);

        Assert.Equal("response.error", events[^1].Event);
        var data = events[^1].Data.GetProperty("data");
        Assert.Equal("provider_error", data.GetProperty("code").GetString());
        Assert.Equal("failed", data.GetProperty("status").GetString());
        Assert.DoesNotContain(events, e => e.Event == "response.completed");

        var assistant = await WaitForAssistantStatusAsync(client, "failed");
        Assert.Equal("MIRA ", assistant.GetProperty("content").GetString());
    }

    [Fact]
    public async Task SlowProviderTimesOutAsInterrupted()
    {
        var provider = new BlockingChatProvider();
        using var factory = new MiraApiFactory(
            new Dictionary<string, string?> { ["Mira:Chat:MaxResponseSeconds"] = "1" },
            s => s.AddSingleton<IChatProvider>(provider));
        using var client = await LoggedInClientAsync(factory, "owner");

        using var response = await client.SendAsync(ClientExtensions.StreamRequest("take forever"));
        var events = await Sse.ReadAllAsync(response);

        Assert.Equal("timeout", events[^1].Data.GetProperty("data").GetProperty("code").GetString());
        await WaitForAssistantStatusAsync(client, "interrupted");
    }

    [Fact]
    public async Task CompetingSendIsRefusedWhileAResponseStreams()
    {
        var provider = new BlockingChatProvider();
        using var factory = new MiraApiFactory(services: s => s.AddSingleton<IChatProvider>(provider));
        using var client = await LoggedInClientAsync(factory, "owner");

        using var first = await client.SendAsync(ClientExtensions.StreamRequest("first"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await first.Content.ReadAsStreamAsync());
        await Sse.ReadUntilAsync(reader, "response.delta");

        using var second = await client.SendAsync(ClientExtensions.StreamRequest("second"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await client.PostAsJsonAsync("/api/chat/cancel", new { });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyMessagesAreRejected(string message)
    {
        using var factory = new MiraApiFactory();
        using var client = await LoggedInClientAsync(factory, "owner");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(ClientExtensions.StreamRequest(message))).StatusCode);
    }

    [Fact]
    public async Task OversizedMessagesAreRejected()
    {
        using var factory = new MiraApiFactory(new Dictionary<string, string?> { ["Mira:Chat:MaxMessageLength"] = "10" });
        using var client = await LoggedInClientAsync(factory, "owner");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(ClientExtensions.StreamRequest("this is far too long"))).StatusCode);
    }

    [Fact]
    public async Task UsersOnlySeeTheirOwnConversation()
    {
        using var factory = new MiraApiFactory();
        using var alice = await LoggedInClientAsync(factory, "alice");
        using var bob = await LoggedInClientAsync(factory, "bob");

        using (var response = await alice.SendAsync(ClientExtensions.StreamRequest("alice's secret plan")))
        {
            await Sse.ReadAllAsync(response);
        }

        var aliceConversation = await alice.GetFromJsonAsync<JsonElement>("/api/conversations/current");
        var bobConversation = await bob.GetFromJsonAsync<JsonElement>("/api/conversations/current");
        Assert.Equal(2, aliceConversation.GetProperty("messages").GetArrayLength());
        Assert.Equal(0, bobConversation.GetProperty("messages").GetArrayLength());
        Assert.NotEqual(aliceConversation.GetProperty("id").GetString(), bobConversation.GetProperty("id").GetString());
        Assert.DoesNotContain("secret", bobConversation.GetRawText());
        Assert.False(aliceConversation.TryGetProperty("ownerUserId", out _));
    }

    [Fact]
    public async Task OwnerIdInTheBodyIsIgnored()
    {
        using var factory = new MiraApiFactory();
        var alice = await factory.CreateUserAsync("alice", Password);
        using var bob = await LoggedInClientAsync(factory, "bob");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = JsonContent.Create(new { message = "hi", ownerUserId = alice.Id, userId = alice.Id })
        };
        using (var response = await bob.SendAsync(request)) await Sse.ReadAllAsync(response);

        var aliceTurns = await factory.Services.GetRequiredService<IConversationStore>().GetPromptTurnsAsync(alice.Id);
        Assert.Empty(aliceTurns);
    }

    [Fact]
    public async Task FollowUpTurnsIncludeEarlierCompletedMessages()
    {
        var provider = new RecordingChatProvider();
        using var factory = new MiraApiFactory(services: s => s.AddSingleton<IChatProvider>(provider));
        using var client = await LoggedInClientAsync(factory, "owner");

        using (var r = await client.SendAsync(ClientExtensions.StreamRequest("one"))) await Sse.ReadAllAsync(r);
        using (var r = await client.SendAsync(ClientExtensions.StreamRequest("two"))) await Sse.ReadAllAsync(r);

        Assert.Equal(
            [new ChatTurn(ChatRole.User, "one"), new ChatTurn(ChatRole.Assistant, "ok"), new ChatTurn(ChatRole.User, "two")],
            provider.LastRequest!.Messages);
    }

    private static async Task<HttpClient> LoggedInClientAsync(MiraApiFactory factory, string username)
    {
        await factory.CreateUserAsync(username, Password);
        var client = factory.CreateClient();
        var login = await client.LoginAsync(username, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> WaitForAssistantStatusAsync(HttpClient client, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            var conversation = await client.GetFromJsonAsync<JsonElement>("/api/conversations/current");
            last = conversation.GetProperty("messages").EnumerateArray().Last();
            if (last.GetProperty("status").GetString() == status) return last;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Assistant message never reached '{status}'; last was {last}.");
    }

    // Emits one delta, then waits until cancelled.
    private sealed class BlockingChatProvider : IChatProvider
    {
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ModelId => "blocking-test";

        public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "partial ";
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Cancelled.TrySetResult(cancellationToken.IsCancellationRequested);
            }

            yield return "never";
        }
    }

    private sealed class RecordingChatProvider : IChatProvider
    {
        public ChatRequest? LastRequest { get; private set; }

        public string ModelId => "recording-test";

        public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await Task.Yield();
            yield return "ok";
        }
    }
}
