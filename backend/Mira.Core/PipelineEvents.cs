namespace Mira.Core;

// Pipeline events feed the M2 inspector (GET /api/diagnostics/...). They hold timings and allowlisted metadata only:
// never message text, prompts, titles, usernames, passwords, cookies, CSRF tokens, API keys or provider error messages.
public static class PipelineEventTypes
{
    public const string RequestStarted = "request.started";
    public const string FirstToken = "response.first_token";
    public const string Completed = "response.completed";
    public const string Cancelled = "response.cancelled";     // user pressed Stop (POST /api/chat/cancel)
    public const string ClientAborted = "response.aborted";   // browser dropped the request
    public const string TimedOut = "response.timeout";
    public const string Failed = "response.failed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        RequestStarted, FirstToken, Completed, Cancelled, ClientAborted, TimedOut, Failed
    };
}

public static class PipelineErrorCodes
{
    public const string Timeout = "timeout";
    public const string ProviderError = "provider_error";
    public const string ClientAborted = "client_aborted";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Timeout, ProviderError, ClientAborted, Cancelled
    };
}

// The complete allowlist of payload fields. Anything not listed here cannot be stored. Stored as JSON in PipelineEvents.RedactedPayload.
public sealed record PipelineEventData(
    string? ModelId = null,          // configured model identifier, validated by the redactor
    MessageStatus? Status = null,
    int? InputChars = null,          // length of the user message, never its text
    int? OutputChars = null,         // length of the assistant text so far
    int? PromptTurns = null,         // number of turns sent to the model
    int? DeltaCount = null,
    long? FirstTokenMs = null,
    long? TotalMs = null,
    string? ErrorCode = null);       // one of PipelineErrorCodes.All

public sealed record PipelineEvent(
    Guid Id,
    Guid OwnerUserId,
    string RequestId,
    Guid? ConversationId,
    string EventType,
    long ElapsedMs,                  // since the request started
    PipelineEventData Data,
    DateTimeOffset CreatedAt);

public sealed record PipelineRequestSummary(
    string RequestId,
    Guid? ConversationId,
    DateTimeOffset StartedAt,
    string LastEventType,
    long? FirstTokenMs,
    long? TotalMs);

public static class PipelineEventRetention
{
    // Owner decision 2026-11-16: keep inspector events 30 days; purge at startup and daily.
    public static readonly TimeSpan Period = TimeSpan.FromDays(30);

    public const int MaxRequestsPerQuery = 50;
}

// Normalizes data to the allowlist: drops a ModelId that isn't a short identifier, drops unknown error codes,
// clamps negative numbers to null. Throws ArgumentException for an event type not in PipelineEventTypes.All.
public interface IPipelineRedactor
{
    PipelineEventData Redact(string eventType, PipelineEventData data);
}

// Called by the chat endpoint. Redacts, stamps Id/CreatedAt and appends to the store.
// Must never throw into the chat turn or cancel it: failures are logged by exception type only and swallowed.
public interface IPipelineRecorder
{
    Task RecordAsync(
        Guid ownerUserId,
        string requestId,
        Guid? conversationId,
        string eventType,
        long elapsedMs,
        PipelineEventData data,
        CancellationToken cancellationToken = default);
}

// Every read is owner-scoped; another owner's request looks like a missing one (empty result).
public interface IPipelineEventStore
{
    // The SQLite store silently drops an event whose ConversationId no longer exists for that owner (conversation deleted
    // mid-turn), so permanent delete leaves nothing behind. Events with a null ConversationId are always stored.
    Task AppendAsync(PipelineEvent pipelineEvent, CancellationToken cancellationToken = default);

    // Oldest first. Empty when missing or not owned.
    Task<IReadOnlyList<PipelineEvent>> GetByRequestAsync(Guid ownerUserId, string requestId, CancellationToken cancellationToken = default);

    // Newest first, at most take (1..PipelineEventRetention.MaxRequestsPerQuery). conversationId null = all of the owner's requests.
    Task<IReadOnlyList<PipelineRequestSummary>> ListRecentRequestsAsync(Guid ownerUserId, Guid? conversationId, int take, CancellationToken cancellationToken = default);

    // Deletes events created before the cutoff, across all owners (maintenance). Returns rows deleted.
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}
