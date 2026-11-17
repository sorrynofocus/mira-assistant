using System.Collections.Concurrent;

namespace Mira.Api.Chat;

// One in-flight chat turn per user. Competing sends are refused rather than interleaved.
public sealed class ActiveTurnRegistry
{
    // How long DELETE /api/conversations/{id} waits for the cancelled turn to wind down and free the user's slot.
    public static readonly TimeSpan DeleteWait = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, ActiveTurn> _active = new();

    public CancellationTokenSource? TryBegin(Guid userId, Guid conversationId)
    {
        var turn = new ActiveTurn(new CancellationTokenSource(), conversationId);
        if (_active.TryAdd(userId, turn)) return turn.Cancellation;

        turn.Cancellation.Dispose();
        return null;
    }

    public void End(Guid userId, CancellationTokenSource cancellation)
    {
        if (_active.TryGetValue(userId, out var turn) && ReferenceEquals(turn.Cancellation, cancellation))
        {
            _active.TryRemove(KeyValuePair.Create(userId, turn));
            turn.Ended.TrySetResult();
        }

        cancellation.Dispose();
    }

    public bool Cancel(Guid userId) => _active.TryGetValue(userId, out var turn) && TryCancel(turn);

    // Owner decision 2026-11-16 23:01: deleting a conversation cancels its active turn. Cancels the owner's turn only if it
    // belongs to that conversation, then waits (bounded) until the turn has ended so the streaming slot is free on return.
    public async Task<bool> CancelConversationAsync(Guid userId, Guid conversationId, TimeSpan wait)
    {
        if (!_active.TryGetValue(userId, out var turn) || turn.ConversationId != conversationId) return false;
        if (!TryCancel(turn)) return false;

        try
        {
            await turn.Ended.Task.WaitAsync(wait);
        }
        catch (TimeoutException)
        {
            // The provider ignored cancellation for too long; the slot frees when the turn finally ends.
        }

        return true;
    }

    private static bool TryCancel(ActiveTurn turn)
    {
        try
        {
            turn.Cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private sealed class ActiveTurn(CancellationTokenSource cancellation, Guid conversationId)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Guid ConversationId { get; } = conversationId;
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
