using System.Collections.Concurrent;

namespace Mira.Api.Chat;

// One in-flight chat turn per user. Competing sends are refused rather than interleaved.
public sealed class ActiveTurnRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    public CancellationTokenSource? TryBegin(Guid userId)
    {
        var cancellation = new CancellationTokenSource();
        if (_active.TryAdd(userId, cancellation)) return cancellation;

        cancellation.Dispose();
        return null;
    }

    public void End(Guid userId, CancellationTokenSource cancellation)
    {
        _active.TryRemove(KeyValuePair.Create(userId, cancellation));
        cancellation.Dispose();
    }

    public bool Cancel(Guid userId)
    {
        if (!_active.TryGetValue(userId, out var cancellation)) return false;
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
