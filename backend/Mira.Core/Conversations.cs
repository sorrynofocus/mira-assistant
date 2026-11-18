namespace Mira.Core;

// Persisted messages and prompt history turns only. There is no System value: the instruction channel is
// ChatRequest.Instructions, so a client cannot inject a system role through stored history.
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

// Instructions is the system/instruction channel (PersonaPrompt.Build). It is never a stored message.
public sealed record ChatRequest(IReadOnlyList<ChatTurn> Messages, string? Instructions = null);

// RequestId correlates a message with the pipeline events of the chat turn that wrote it (32 lowercase hex chars, Guid "N" format).
public sealed record ConversationMessage(
    Guid Id,
    ChatRole Role,
    string Content,
    MessageStatus Status,
    DateTimeOffset CreatedAt,
    string? ModelId,
    string? RequestId);

public sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid PersonaId);

public sealed record ConversationSnapshot(
    Guid Id,
    Guid OwnerUserId,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationMessage> Messages,
    Guid PersonaId);

public static class ConversationLimits
{
    public const int MaxTitleLength = 200;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    // Null, empty or whitespace means "untitled". Throws if the trimmed title is too long; callers validate first and return 400.
    public static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var trimmed = title.Trim();
        if (trimmed.Length > MaxTitleLength)
        {
            throw new ArgumentException($"Titles are limited to {MaxTitleLength} characters.", nameof(title));
        }

        return trimmed;
    }
}

// M2: several conversations per owner. Every method is scoped by the authenticated owner taken from the principal;
// there is no lookup by bare ID. A conversation or message that doesn't exist and one owned by someone else look the same:
// reads return null/false/empty, and writes to a message throw KeyNotFoundException. Endpoints map both to 404.
// Owner IDs must not be Guid.Empty (ArgumentException).
public interface IConversationStore
{
    // Newest activity first (UpdatedAt descending, then Id). take must be 1..ConversationLimits.MaxPageSize; skip >= 0.
    Task<IReadOnlyList<ConversationSummary>> ListAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default);

    // Pins the new conversation to personaId (a concrete id, never "follow the default"). personaId must not be Guid.Empty.
    Task<ConversationSummary> CreateAsync(Guid ownerUserId, string? title, Guid personaId, CancellationToken cancellationToken = default);

    // Compatibility default: pins to the built-in MIRA. Chunk A callers pass a copy of the owner's default instead.
    Task<ConversationSummary> CreateAsync(Guid ownerUserId, string? title, CancellationToken cancellationToken = default) =>
        CreateAsync(ownerUserId, title, PersonaIds.BuiltInMira, cancellationToken);

    // Messages oldest first. Null when missing or not owned.
    Task<ConversationSnapshot?> GetAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default);

    // M1 compatibility (GET /api/conversations/current and chat/stream without a conversationId):
    // the owner's most recently updated conversation, or a new untitled one if the owner has none.
    // personaIdForNew is used only when the owner has no conversations yet; existing rows keep their stored pin.
    Task<ConversationSnapshot> GetMostRecentOrCreateAsync(Guid ownerUserId, Guid personaIdForNew, CancellationToken cancellationToken = default);

    // Compatibility default: new conversations are pinned to the built-in MIRA.
    Task<ConversationSnapshot> GetMostRecentOrCreateAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
        GetMostRecentOrCreateAsync(ownerUserId, PersonaIds.BuiltInMira, cancellationToken);

    // False when missing or not owned. Title rules: ConversationLimits.NormalizeTitle. Bumps UpdatedAt.
    Task<bool> RenameAsync(Guid ownerUserId, Guid conversationId, string? title, CancellationToken cancellationToken = default);

    // Permanent (owner decision 2026-11-16): removes the conversation and its messages. The SQLite implementation also removes
    // the conversation's PipelineEvents rows in the same transaction. False when missing or not owned.
    Task<bool> DeleteAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default);

    // The three writes below throw KeyNotFoundException when the conversation is missing or not owned. Each bumps UpdatedAt.
    Task<ConversationMessage> AppendUserMessageAsync(Guid ownerUserId, Guid conversationId, string content, string requestId, CancellationToken cancellationToken = default);

    Task<ConversationMessage> BeginAssistantMessageAsync(Guid ownerUserId, Guid conversationId, string modelId, string requestId, CancellationToken cancellationToken = default);

    // status must not be Streaming (ArgumentException). Throws KeyNotFoundException if the assistant message isn't in that owned conversation.
    Task FinishAssistantMessageAsync(Guid ownerUserId, Guid conversationId, Guid messageId, string content, MessageStatus status, CancellationToken cancellationToken = default);

    // Turns sent to the model: user messages and completed assistant replies, oldest first. Empty when missing or not owned.
    // Never includes an instruction/system turn; callers put PersonaPrompt.Build into ChatRequest.Instructions.
    Task<IReadOnlyList<ChatTurn>> GetPromptTurnsAsync(Guid ownerUserId, Guid conversationId, CancellationToken cancellationToken = default);

    // Soft pin: stores personaId as-is (no FK). False when the conversation is missing or not owned.
    // personaId must not be Guid.Empty. This method does not verify that the persona still exists.
    Task<bool> SetPersonaAsync(Guid ownerUserId, Guid conversationId, Guid personaId, CancellationToken cancellationToken = default);
}

