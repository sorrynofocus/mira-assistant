using Mira.Core;

namespace Mira.Infrastructure.Pipeline;

// Process-memory IPipelineEventStore: a test double for API tests, owner-scoped with the same semantics as SqlitePipelineEventStore.
public sealed class InMemoryPipelineEventStore : IPipelineEventStore
{
    private readonly object _gate = new();
    private readonly List<PipelineEvent> _events = [];

    public Task AppendAsync(PipelineEvent pipelineEvent, CancellationToken cancellationToken = default)
    {
        Validate(pipelineEvent);
        lock (_gate)
        {
            _events.Add(pipelineEvent);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PipelineEvent>> GetByRequestAsync(Guid ownerUserId, string requestId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<PipelineEvent> events = _events
                .Where(e => e.OwnerUserId == ownerUserId && e.RequestId == requestId)
                .OrderBy(e => e.CreatedAt) // stable: ties keep append order
                .ToArray();
            return Task.FromResult(events);
        }
    }

    public Task<IReadOnlyList<PipelineRequestSummary>> ListRecentRequestsAsync(Guid ownerUserId, Guid? conversationId, int take, CancellationToken cancellationToken = default)
    {
        ValidateTake(take);
        lock (_gate)
        {
            var owned = _events
                .Where(e => e.OwnerUserId == ownerUserId && (conversationId is null || e.ConversationId == conversationId))
                .OrderBy(e => e.CreatedAt)
                .ToArray();
            return Task.FromResult(Summarize(owned, take));
        }
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.RemoveAll(e => e.CreatedAt < cutoff));
        }
    }

    // Shared with SqlitePipelineEventStore so both stores summarize identically.
    // events: one owner's events, oldest first (ties in append order). Newest request (by start time) first, at most take.
    internal static IReadOnlyList<PipelineRequestSummary> Summarize(IEnumerable<PipelineEvent> events, int take) =>
        events
            .GroupBy(e => e.RequestId, StringComparer.Ordinal)
            .Select(g =>
            {
                var ordered = g.ToArray();
                return new PipelineRequestSummary(
                    g.Key,
                    ordered.Select(e => e.ConversationId).FirstOrDefault(id => id is not null),
                    ordered[0].CreatedAt,
                    ordered[^1].EventType,
                    ordered.Select(e => e.Data.FirstTokenMs).FirstOrDefault(v => v is not null),
                    ordered.Select(e => e.Data.TotalMs).LastOrDefault(v => v is not null));
            })
            .OrderByDescending(s => s.StartedAt)
            .ThenBy(s => s.RequestId, StringComparer.Ordinal)
            .Take(take)
            .ToArray();

    internal static void Validate(PipelineEvent pipelineEvent)
    {
        ArgumentNullException.ThrowIfNull(pipelineEvent);
        ArgumentNullException.ThrowIfNull(pipelineEvent.Data);
        if (pipelineEvent.OwnerUserId == Guid.Empty) throw new ArgumentException("An authenticated owner is required.", nameof(pipelineEvent));
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineEvent.RequestId);
        if (!PipelineEventTypes.All.Contains(pipelineEvent.EventType))
        {
            throw new ArgumentException("Unknown pipeline event type.", nameof(pipelineEvent));
        }
    }

    internal static void ValidateTake(int take)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, PipelineEventRetention.MaxRequestsPerQuery);
    }
}
