using System.Collections.Concurrent;
using SoulRemote.Abstractions;

namespace SoulRemote.Services;

/// <summary>
/// A per-key sliding window, used to keep one chat from driving the machine faster
/// than a person could mean to.
///
/// This is not a security boundary — only paired chats get this far. It is there so
/// a held-down button, a retry storm on a bad link, or a paste of twenty commands
/// cannot queue up hundreds of shutdown taps behind each other. Unauthenticated
/// senders are limited on a separate, much tighter key, which is what stops a
/// stranger turning the bot into an outbound message pump.
/// </summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<long, Queue<DateTime>> _hits = new();
    private readonly IClock _clock;
    private readonly int _limit;
    private readonly TimeSpan _window;

    public RateLimiter(int limit, TimeSpan window, IClock? clock = null)
    {
        _limit = Math.Max(1, limit);
        _window = window <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : window;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Records an attempt and says whether it is within the budget.</summary>
    public bool Allow(long key)
    {
        var now = _clock.UtcNow;
        var queue = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > _window)
                queue.Dequeue();
            if (queue.Count >= _limit)
                return false;
            queue.Enqueue(now);
            return true;
        }
    }

    /// <summary>Clears one key's history — used when a chat is un-paired.</summary>
    public void Forget(long key) => _hits.TryRemove(key, out _);
}
