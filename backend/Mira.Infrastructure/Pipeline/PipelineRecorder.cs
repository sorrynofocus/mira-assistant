using Microsoft.Extensions.Logging;
using Mira.Core;

namespace Mira.Infrastructure.Pipeline;

// Redacts, stamps Id/CreatedAt and appends. Never throws into the chat turn: every failure is logged by exception type only
// (no payload, no message) and swallowed. The write ignores the chat's cancellation token so a cancelled or aborted turn
// still records its terminal event; a short timeout bounds it instead.
public sealed partial class PipelineRecorder(
    IPipelineEventStore store,
    IPipelineRedactor redactor,
    TimeProvider time,
    ILogger<PipelineRecorder> logger) : IPipelineRecorder
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    public async Task RecordAsync(
        Guid ownerUserId,
        string requestId,
        Guid? conversationId,
        string eventType,
        long elapsedMs,
        PipelineEventData data,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var redacted = redactor.Redact(eventType, data);
            var pipelineEvent = new PipelineEvent(
                Guid.NewGuid(), ownerUserId, requestId, conversationId, eventType, Math.Max(0, elapsedMs), redacted, time.GetUtcNow());
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await store.AppendAsync(pipelineEvent, timeout.Token);
        }
        catch (Exception exception)
        {
            LogRecordFailed(logger, exception.GetType().Name);
        }
    }

    // Deliberately no exception object: provider or store messages could carry data.
    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Pipeline event not recorded ({ErrorType}).")]
    private static partial void LogRecordFailed(ILogger logger, string errorType);
}
