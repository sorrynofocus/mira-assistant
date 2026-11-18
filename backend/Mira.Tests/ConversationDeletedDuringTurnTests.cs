using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mira.Core;
using Mira.Infrastructure.Sqlite;

namespace Mira.Tests;

// Review Major 1: permanently deleting a conversation while its turn is running must leave no pipeline events behind
// (diagnostics 404s) and must tell the client conversation_deleted rather than completed.
public sealed class ConversationDeletedDuringTurnTests
{
    public enum Gate { BeforeFirstToken, MidStream, AtFinish }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Owner decision 2026-11-16 23:01: the delete cancels the turn (no gate release needed) and frees the streaming slot.
    [Theory]
    [InlineData(Gate.BeforeFirstToken)]
    [InlineData(Gate.MidStream)]
    [InlineData(Gate.AtFinish)]
    public async Task DeletingTheConversationDuringATurnCancelsItLeavesNothingAndReportsConversationDeleted(Gate gate)
    {
        var provider = new GatedChatProvider(gate);
        using var factory = new MiraApiFactory(services: s => s.AddSingleton<IChatProvider>(provider));
        using var client = await factory.LoggedInClientAsync("owner");
        var conversation = await client.CreateConversationAsync();

        using var response = await client.SendAsync(ClientExtensions.StreamRequest("hello", conversation), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await provider.Waiting.Task.WaitAsync(Timeout);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/conversations/{conversation}")).StatusCode);
        Assert.True(provider.CancellationObserved.Task.IsCompleted, "The delete should have cancelled the provider before returning.");

        // The slot is free as soon as DELETE returns: a new send is accepted, not 409.
        var next = await client.CreateConversationAsync();
        using (var again = await client.SendAsync(ClientExtensions.StreamRequest("again", next)))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Contains(await Sse.ReadAllAsync(again).WaitAsync(Timeout), e => e.Event == "response.completed");
        }

        var events = await Sse.ReadAllAsync(response).WaitAsync(Timeout);
        var requestId = events[0].Data.GetProperty("requestId").GetString()!;
        Assert.DoesNotContain(events, e => e.Event is "response.completed" or "response.cancelled");
        var error = Assert.Single(events, e => e.Event == "response.error");
        Assert.Equal(events[^1], error);
        Assert.Equal("conversation_deleted", error.Data.GetProperty("data").GetProperty("code").GetString());
        Assert.Equal("interrupted", error.Data.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(1, error.Data.GetProperty("v").GetInt32());
        Assert.Equal(requestId, error.Data.GetProperty("requestId").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/diagnostics/{requestId}")).StatusCode);
        var owner = (await client.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("id").GetGuid();
        Assert.Empty(await factory.Services.GetRequiredService<IPipelineEventStore>().GetByRequestAsync(owner, requestId));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/conversations/{conversation}")).StatusCode);
    }

    [Fact]
    public async Task DeletingAnotherConversationDoesNotCancelTheActiveTurn()
    {
        var provider = new GatedChatProvider(Gate.MidStream);
        using var factory = new MiraApiFactory(services: s => s.AddSingleton<IChatProvider>(provider));
        using var client = await factory.LoggedInClientAsync("owner");
        var streaming = await client.CreateConversationAsync();
        var other = await client.CreateConversationAsync();

        using var response = await client.SendAsync(ClientExtensions.StreamRequest("hello", streaming), HttpCompletionOption.ResponseHeadersRead);
        await provider.Waiting.Task.WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/conversations/{other}")).StatusCode);
        Assert.False(provider.CancellationObserved.Task.IsCompleted);

        provider.Release.SetResult();
        var events = await Sse.ReadAllAsync(response).WaitAsync(Timeout);
        Assert.Equal("response.completed", events[^1].Event);
    }

    [Fact]
    public async Task DeletingBeforeTheStreamStartsGives404AndRecordsNothing()
    {
        using var factory = new MiraApiFactory(services: s => s.AddSingleton<IConversationStore>(sp =>
            new DeleteDuringPromptStore(ActivatorUtilities.CreateInstance<SqliteConversationStore>(sp))));
        using var client = await factory.LoggedInClientAsync("owner");
        var conversation = await client.CreateConversationAsync();

        using var response = await client.SendAsync(ClientExtensions.StreamRequest("hello", conversation));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/diagnostics");
        Assert.Empty(list.GetProperty("items").EnumerateArray());
    }

    // Pauses only its first stream at the chosen point; later streams run straight through.
    private sealed class GatedChatProvider(Gate gate) : IChatProvider
    {
        private int _calls;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ModelId => "gated-test";

        public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var gated = Interlocked.Increment(ref _calls) == 1;
            if (gated && gate == Gate.BeforeFirstToken) await PauseAsync(cancellationToken);
            yield return "first ";
            if (gated && gate == Gate.MidStream) await PauseAsync(cancellationToken);
            yield return "last";
            if (gated && gate == Gate.AtFinish) await PauseAsync(cancellationToken);
        }

        private async Task PauseAsync(CancellationToken cancellationToken)
        {
            Waiting.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    // Deletes the conversation between appending the user message and starting the stream (the old unguarded window).
    private sealed class DeleteDuringPromptStore(IConversationStore inner) : IConversationStore
    {
        public Task<IReadOnlyList<ConversationSummary>> ListAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
            inner.ListAsync(ownerUserId, skip, take, cancellationToken);
        public Task<ConversationSummary> CreateAsync(Guid ownerUserId, string? title, Guid personaId, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(ownerUserId, title, personaId, cancellationToken);
        public Task<ConversationSnapshot?> GetAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(ownerUserId, conversationId, cancellationToken);
        public Task<ConversationSnapshot> GetMostRecentOrCreateAsync(Guid ownerUserId, Guid personaIdForNew, CancellationToken cancellationToken = default) =>
            inner.GetMostRecentOrCreateAsync(ownerUserId, personaIdForNew, cancellationToken);
        public Task<bool> SetPersonaAsync(Guid ownerUserId, Guid conversationId, Guid personaId, CancellationToken cancellationToken = default) =>
            inner.SetPersonaAsync(ownerUserId, conversationId, personaId, cancellationToken);
        public Task<bool> RenameAsync(Guid ownerUserId, Guid conversationId, string? title, CancellationToken cancellationToken = default) =>
            inner.RenameAsync(ownerUserId, conversationId, title, cancellationToken);
        public Task<bool> DeleteAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(ownerUserId, conversationId, cancellationToken);
        public Task<ConversationMessage> AppendUserMessageAsync(Guid ownerUserId, Guid conversationId, string content, string requestId, CancellationToken cancellationToken = default) =>
            inner.AppendUserMessageAsync(ownerUserId, conversationId, content, requestId, cancellationToken);
        public Task<ConversationMessage> BeginAssistantMessageAsync(Guid ownerUserId, Guid conversationId, string modelId, string requestId, CancellationToken cancellationToken = default) =>
            inner.BeginAssistantMessageAsync(ownerUserId, conversationId, modelId, requestId, cancellationToken);
        public Task FinishAssistantMessageAsync(Guid ownerUserId, Guid conversationId, Guid messageId, string content, MessageStatus status, CancellationToken cancellationToken = default) =>
            inner.FinishAssistantMessageAsync(ownerUserId, conversationId, messageId, content, status, cancellationToken);

        public async Task<IReadOnlyList<ChatTurn>> GetPromptTurnsAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default)
        {
            await inner.DeleteAsync(ownerUserId, conversationId, cancellationToken);
            return await inner.GetPromptTurnsAsync(ownerUserId, conversationId, cancellationToken);
        }
    }
}
