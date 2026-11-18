using Mira.Core;

namespace Mira.Infrastructure;

// Process-memory IConversationStore; everything is lost on restart. The host uses it until M2 chunk A's SqliteConversationStore
// is wired in; afterwards it remains a test double. It holds no pipeline events, so DeleteAsync removes only the conversation.
public sealed class InMemoryConversationStore(TimeProvider time) : IConversationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Conversation> _conversations = new();

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, ConversationLimits.MaxPageSize);
        lock (_gate)
        {
            IReadOnlyList<ConversationSummary> page = Owned(ownerUserId)
                .OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Id)
                .Skip(skip).Take(take)
                .Select(Summary)
                .ToArray();
            return Task.FromResult(page);
        }
    }

    public Task<ConversationSummary> CreateAsync(Guid ownerUserId, string? title, CancellationToken cancellationToken = default) =>
        CreateAsync(ownerUserId, title, PersonaIds.BuiltInMira, cancellationToken);

    public Task<ConversationSummary> CreateAsync(Guid ownerUserId, string? title, Guid personaId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        RequirePersona(personaId);
        var normalized = ConversationLimits.NormalizeTitle(title);
        lock (_gate)
        {
            return Task.FromResult(Summary(Add(ownerUserId, normalized, personaId)));
        }
    }

    public Task<ConversationSnapshot?> GetAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            return Task.FromResult(Find(ownerUserId, conversationId) is { } c ? Snapshot(c) : null);
        }
    }

    public Task<ConversationSnapshot> GetMostRecentOrCreateAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
        GetMostRecentOrCreateAsync(ownerUserId, PersonaIds.BuiltInMira, cancellationToken);

    public Task<ConversationSnapshot> GetMostRecentOrCreateAsync(Guid ownerUserId, Guid personaIdForNew, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        RequirePersona(personaIdForNew);
        lock (_gate)
        {
            var conversation = Owned(ownerUserId).OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Id).FirstOrDefault()
                ?? Add(ownerUserId, null, personaIdForNew);
            return Task.FromResult(Snapshot(conversation));
        }
    }

    public Task<bool> SetPersonaAsync(Guid ownerUserId, Guid conversationId, Guid personaId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        RequirePersona(personaId);
        lock (_gate)
        {
            if (Find(ownerUserId, conversationId) is not { } c) return Task.FromResult(false);
            c.PersonaId = personaId;
            c.UpdatedAt = time.GetUtcNow();
            return Task.FromResult(true);
        }
    }

    public Task<bool> RenameAsync(Guid ownerUserId, Guid conversationId, string? title, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        var normalized = ConversationLimits.NormalizeTitle(title);
        lock (_gate)
        {
            if (Find(ownerUserId, conversationId) is not { } c) return Task.FromResult(false);
            c.Title = normalized;
            c.UpdatedAt = time.GetUtcNow();
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            return Task.FromResult(Find(ownerUserId, conversationId) is not null && _conversations.Remove(conversationId));
        }
    }

    public Task<ConversationMessage> AppendUserMessageAsync(Guid ownerUserId, Guid conversationId, string content, string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return Task.FromResult(Append(ownerUserId, conversationId, ChatRole.User, content, MessageStatus.Completed, null, requestId));
    }

    public Task<ConversationMessage> BeginAssistantMessageAsync(Guid ownerUserId, Guid conversationId, string modelId, string requestId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Append(ownerUserId, conversationId, ChatRole.Assistant, string.Empty, MessageStatus.Streaming, modelId, requestId));

    public Task FinishAssistantMessageAsync(Guid ownerUserId, Guid conversationId, Guid messageId, string content, MessageStatus status, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        if (status == MessageStatus.Streaming) throw new ArgumentException("A finished message cannot remain streaming.", nameof(status));

        lock (_gate)
        {
            var conversation = Find(ownerUserId, conversationId) ?? throw new KeyNotFoundException("Conversation not found for this owner.");
            var index = conversation.Messages.FindIndex(m => m.Id == messageId && m.Role == ChatRole.Assistant);
            if (index < 0) throw new KeyNotFoundException("Assistant message not found for this owner.");
            conversation.Messages[index] = conversation.Messages[index] with { Content = content, Status = status };
            conversation.UpdatedAt = time.GetUtcNow();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatTurn>> GetPromptTurnsAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            IReadOnlyList<ChatTurn> turns = Find(ownerUserId, conversationId) is { } c
                ? c.Messages
                    .Where(m => m.Role == ChatRole.User || m.Status == MessageStatus.Completed)
                    .Select(m => new ChatTurn(m.Role, m.Content))
                    .ToArray()
                : [];
            return Task.FromResult(turns);
        }
    }

    private ConversationMessage Append(Guid ownerUserId, Guid conversationId, ChatRole role, string content, MessageStatus status, string? modelId, string requestId)
    {
        RequireOwner(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (_gate)
        {
            var conversation = Find(ownerUserId, conversationId) ?? throw new KeyNotFoundException("Conversation not found for this owner.");
            var now = time.GetUtcNow();
            var message = new ConversationMessage(Guid.NewGuid(), role, content, status, now, modelId, requestId);
            conversation.Messages.Add(message);
            conversation.UpdatedAt = now;
            return message;
        }
    }

    // Callers hold _gate.
    private Conversation Add(Guid ownerUserId, string? title, Guid personaId)
    {
        var now = time.GetUtcNow();
        var conversation = new Conversation(Guid.NewGuid(), ownerUserId, now)
        {
            Title = title,
            UpdatedAt = now,
            PersonaId = personaId
        };
        _conversations.Add(conversation.Id, conversation);
        return conversation;
    }

    private Conversation? Find(Guid ownerUserId, Guid conversationId) =>
        _conversations.TryGetValue(conversationId, out var c) && c.OwnerUserId == ownerUserId ? c : null;

    private IEnumerable<Conversation> Owned(Guid ownerUserId) => _conversations.Values.Where(c => c.OwnerUserId == ownerUserId);

    private static ConversationSummary Summary(Conversation c) => new(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.PersonaId);

    private static ConversationSnapshot Snapshot(Conversation c) =>
        new(c.Id, c.OwnerUserId, c.Title, c.CreatedAt, c.UpdatedAt, c.Messages.ToArray(), c.PersonaId);

    private static void RequireOwner(Guid ownerUserId)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("An authenticated owner is required.", nameof(ownerUserId));
    }

    private static void RequirePersona(Guid personaId)
    {
        if (personaId == Guid.Empty) throw new ArgumentException("A persona id is required.", nameof(personaId));
    }

    private sealed class Conversation(Guid id, Guid ownerUserId, DateTimeOffset createdAt)
    {
        public Guid Id { get; } = id;
        public Guid OwnerUserId { get; } = ownerUserId;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public string? Title { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public Guid PersonaId { get; set; } = PersonaIds.BuiltInMira;
        public List<ConversationMessage> Messages { get; } = [];
    }
}
