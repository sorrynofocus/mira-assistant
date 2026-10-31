namespace Mira.Core;

public enum ChatRole
{
    User,
    Assistant
}

public enum MessageStatus
{
    Streaming,
    Completed,
    Interrupted,
    Failed
}

public sealed record ChatTurn(ChatRole Role, string Content);

public sealed record ChatRequest(IReadOnlyList<ChatTurn> Messages);

public sealed record ConversationMessage(
    Guid Id,
    ChatRole Role,
    string Content,
    MessageStatus Status,
    DateTimeOffset CreatedAt,
    string? ModelId);

public sealed record ConversationSnapshot(Guid Id, Guid OwnerUserId, IReadOnlyList<ConversationMessage> Messages);

// M1 keeps one conversation per user. Every method is scoped by the authenticated owner; there is no lookup by bare ID.
public interface IConversationStore
{
    Task<ConversationSnapshot> GetOrCreateAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    Task<ConversationMessage> AppendUserMessageAsync(Guid ownerUserId, string content, CancellationToken cancellationToken = default);

    Task<ConversationMessage> BeginAssistantMessageAsync(Guid ownerUserId, string modelId, CancellationToken cancellationToken = default);

    Task FinishAssistantMessageAsync(Guid ownerUserId, Guid messageId, string content, MessageStatus status, CancellationToken cancellationToken = default);

    // Turns sent to the model: user messages and completed assistant replies, oldest first.
    Task<IReadOnlyList<ChatTurn>> GetPromptTurnsAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}
