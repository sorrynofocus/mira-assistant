using Mira.Core;

namespace Mira.Api.Diagnostics;

// Deletes inspector events older than PipelineEventRetention.Period (30 days, owner decision 2026-11-16):
// once when the API starts, then every 24 hours. Failures are logged by exception type and retried next cycle.
public sealed partial class PipelineRetentionService(
    IPipelineEventStore store,
    TimeProvider time,
    ILogger<PipelineRetentionService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public async Task<int> PurgeOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var removed = await store.PurgeOlderThanAsync(time.GetUtcNow() - PipelineEventRetention.Period, cancellationToken);
            LogPurged(logger, removed);
            return removed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPurgeFailed(logger, ex.GetType().Name);
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PurgeOnceAsync(stoppingToken);
                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down.
        }
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Debug, Message = "Pipeline retention removed {Count} events.")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "Pipeline retention purge failed ({ErrorType}).")]
    private static partial void LogPurgeFailed(ILogger logger, string errorType);
}
