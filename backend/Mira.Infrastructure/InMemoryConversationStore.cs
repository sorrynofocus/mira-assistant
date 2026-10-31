using System.Collections.Concurrent;
using Mira.Core;

namespace Mira.Infrastructure;

// M1 keeps conversations in process memory; they are lost on restart. M2 replaces this with SQLite persistence.
public sealed class InMemoryConversationStore(TimeProvider time) : IConversationStore
{
    private readonly ConcurrentDictionary<Guid, ConversationState> _byOwner = new();

    public Task<ConversationSnapshot> GetOrCreateAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var state = GetState(ownerUserId);
        lock (state.Gate)
        {
            return Task.FromResult(new ConversationSnapshot(state.Id, ownerUserId, state.Messages.ToArray()));
        }
    }

    public Task<ConversationMessage> AppendUserMessageAsync(Guid ownerUserId, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return Task.FromResult(Append(ownerUserId, new ConversationMessage(
            Guid.NewGuid(), ChatRole.User, content, MessageStatus.Completed, time.GetUtcNow(), null)));
    }

    public Task<ConversationMessage> BeginAssistantMessageAsync(Guid ownerUserId, string modelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Append(ownerUserId, new ConversationMessage(
            Guid.NewGuid(), ChatRole.Assistant, string.Empty, MessageStatus.Streaming, time.GetUtcNow(), modelId)));

    public Task FinishAssistantMessageAsync(Guid ownerUserId, Guid messageId, string content, MessageStatus status, CancellationToken cancellationToken = default)
    {
        if (status == MessageStatus.Streaming) throw new ArgumentException("A finished message cannot remain streaming.", nameof(status));

        var state = GetState(ownerUserId);
        lock (state.Gate)
        {
            var index = state.Messages.FindIndex(m => m.Id == messageId && m.Role == ChatRole.Assistant);
            if (index < 0) throw new KeyNotFoundException("Assistant message not found for this owner.");
            state.Messages[index] = state.Messages[index] with { Content = content, Status = status };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatTurn>> GetPromptTurnsAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var state = GetState(ownerUserId);
        lock (state.Gate)
        {
            IReadOnlyList<ChatTurn> turns = state.Messages
                .Where(m => m.Role == ChatRole.User || m.Status == MessageStatus.Completed)
                .Select(m => new ChatTurn(m.Role, m.Content))
                .ToArray();
            return Task.FromResult(turns);
        }
    }

    private ConversationMessage Append(Guid ownerUserId, ConversationMessage message)
    {
        var state = GetState(ownerUserId);
        lock (state.Gate)
        {
            state.Messages.Add(message);
        }

        return message;
    }

    private ConversationState GetState(Guid ownerUserId)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("An authenticated owner is required.", nameof(ownerUserId));
        return _byOwner.GetOrAdd(ownerUserId, _ => new ConversationState());
    }

    private sealed class ConversationState
    {
        public Guid Id { get; } = Guid.NewGuid();
        public object Gate { get; } = new();
        public List<ConversationMessage> Messages { get; } = [];
    }
}
