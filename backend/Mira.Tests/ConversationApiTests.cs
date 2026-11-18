using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mira.Core;

namespace Mira.Tests;

public sealed class ConversationApiTests
{
    [Fact]
    public async Task UnauthenticatedRequestsAreRejected()
    {
        using var factory = new MiraApiFactory();
        using var client = factory.CreateClient();
        await client.RefreshCsrfAsync();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/conversations", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PatchAsJsonAsync($"/api/conversations/{id}", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/api/conversations/{id}")).StatusCode);
    }

    [Fact]
    public async Task StateChangesRequireTheCsrfHeader()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");
        var id = await client.CreateConversationAsync("keep me");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/conversations", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/conversations/{id}", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/conversations/{id}")).StatusCode);

        var page = await client.GetFromJsonAsync<JsonElement>("/api/conversations");
        var only = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("keep me", only.GetProperty("title").GetString());
    }

    [Fact]
    public async Task CreateListGetRenameAndDeletePermanently()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");

        var created = await client.PostAsJsonAsync("/api/conversations", new { title = "  Trip plans  " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = await created.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = first.GetProperty("id").GetGuid();
        Assert.Equal($"/api/conversations/{firstId}", created.Headers.Location?.OriginalString);
        Assert.Equal("Trip plans", first.GetProperty("title").GetString());

        using var empty = new HttpRequestMessage(HttpMethod.Post, "/api/conversations");
        var untitled = await client.SendAsync(empty); // no body at all
        Assert.Equal(HttpStatusCode.Created, untitled.StatusCode);
        var secondId = (await untitled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var page = await client.GetFromJsonAsync<JsonElement>("/api/conversations?take=1");
        Assert.Equal(secondId, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("items")[0].GetProperty("title").ValueKind);
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        var rest = await client.GetFromJsonAsync<JsonElement>("/api/conversations?skip=1&take=1");
        Assert.Equal(firstId, rest.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.False(rest.GetProperty("hasMore").GetBoolean());

        var renamed = await client.PatchAsJsonAsync($"/api/conversations/{secondId}", new { title = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Renamed", (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        var cleared = await client.PatchAsJsonAsync($"/api/conversations/{secondId}", new { title = "   " });
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").ValueKind);

        var got = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{firstId}");
        Assert.Equal("Trip plans", got.GetProperty("title").GetString());
        Assert.Empty(got.GetProperty("messages").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/conversations/{firstId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/conversations/{firstId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/conversations/{firstId}")).StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/conversations");
        Assert.Equal(secondId, Assert.Single(after.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AnotherUsersConversationIsNotFoundForEveryOperation()
    {
        using var factory = new MiraApiFactory();
        using var alice = await factory.LoggedInClientAsync("alice");
        using var bob = await factory.LoggedInClientAsync("bob");
        var aliceId = await alice.CreateConversationAsync("alice only");

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/conversations/{aliceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PatchAsJsonAsync($"/api/conversations/{aliceId}", new { title = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/conversations/{aliceId}")).StatusCode);
        using (var stream = await bob.SendAsync(ClientExtensions.StreamRequest("hi", aliceId)))
        {
            Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
            Assert.NotEqual("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
        }

        Assert.Empty((await bob.GetFromJsonAsync<JsonElement>("/api/conversations")).GetProperty("items").EnumerateArray());
        var intact = await alice.GetFromJsonAsync<JsonElement>($"/api/conversations/{aliceId}");
        Assert.Equal("alice only", intact.GetProperty("title").GetString());
        Assert.Empty(intact.GetProperty("messages").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/conversations/not-a-guid")).StatusCode);
    }

    [Fact]
    public async Task CurrentReturnsTheSameShapeAsGetById()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");
        var id = await client.CreateConversationAsync("latest");
        using (var r = await client.SendAsync(ClientExtensions.StreamRequest("hello", id))) await Sse.ReadAllAsync(r);

        var current = await client.GetFromJsonAsync<JsonElement>("/api/conversations/current");
        var byId = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{id}");

        Assert.Equal(["id", "title", "createdAt", "updatedAt", "messages", "personaId", "personaStatus"], current.EnumerateObject().Select(p => p.Name));
        Assert.Equal(byId.GetRawText(), current.GetRawText());
        Assert.Equal("latest", current.GetProperty("title").GetString());
        Assert.Equal(["id", "role", "content", "status", "createdAt", "modelId", "requestId"],
            current.GetProperty("messages")[0].EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("?take=0")]
    [InlineData("?take=101")]
    [InlineData("?skip=-1")]
    public async Task PagingIsBounded(string query)
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/conversations" + query)).StatusCode);
    }

    [Fact]
    public async Task OverlongTitlesAreRejected()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");
        var title = new string('t', ConversationLimits.MaxTitleLength + 1);
        var id = await client.CreateConversationAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/conversations", new { title })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/conversations/{id}", new { title })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/conversations", new { title = new string('t', ConversationLimits.MaxTitleLength) })).StatusCode);
    }

    [Fact]
    public async Task MessagesPersistInTheChosenConversationAcrossAnApiRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mira-tests", Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?> { ["Mira:DatabasePath"] = Path.Combine(directory, "mira.db") };
        try
        {
            Guid target, other;
            string requestId;
            using (var factory = new MiraApiFactory(settings))
            using (var client = factory.CreateClient())
            {
                var owner = (await factory.CreateUserAsync("owner", TestUsers.Password)).Id;
                Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("owner", TestUsers.Password)).StatusCode);
                target = await client.CreateConversationAsync("target");
                other = await client.CreateConversationAsync("other");
                using var response = await client.SendAsync(ClientExtensions.StreamRequest("remember this", target));
                var events = await Sse.ReadAllAsync(response);
                requestId = events[0].Data.GetProperty("requestId").GetString()!;
                Assert.Equal(target.ToString(), events[0].Data.GetProperty("conversationId").GetString());

                // Simulate a crash mid-turn in "other": an assistant row left Streaming.
                var store = factory.Services.GetRequiredService<IConversationStore>();
                await store.AppendUserMessageAsync(owner, other, "left hanging", "0123456789abcdef0123456789abcdef");
                await store.BeginAssistantMessageAsync(owner, other, "mock-chat", "0123456789abcdef0123456789abcdef");
            }

            SqliteConnection.ClearAllPools();
            using (var restarted = new MiraApiFactory(settings))
            using (var client = restarted.CreateClient())
            {
                await client.LoginAsync("owner", TestUsers.Password);
                var conversation = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{target}");
                var messages = conversation.GetProperty("messages").EnumerateArray().ToList();
                Assert.Equal(2, messages.Count);
                Assert.Equal("remember this", messages[0].GetProperty("content").GetString());
                Assert.Equal("completed", messages[1].GetProperty("status").GetString());
                Assert.All(messages, m => Assert.Equal(requestId, m.GetProperty("requestId").GetString()));

                // API startup swept the crashed turn.
                var crashed = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{other}");
                Assert.Equal("interrupted", crashed.GetProperty("messages")[1].GetProperty("status").GetString());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task DeletingAConversationRemovesItsPipelineEvents()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");
        var id = await client.CreateConversationAsync();
        using var response = await client.SendAsync(ClientExtensions.StreamRequest("trace me", id));
        var requestId = (await Sse.ReadAllAsync(response))[0].Data.GetProperty("requestId").GetString();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/diagnostics/{requestId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/conversations/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/diagnostics/{requestId}")).StatusCode);
    }
}
