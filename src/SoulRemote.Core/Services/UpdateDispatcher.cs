using System.Collections.Concurrent;

namespace SoulRemote.Services;

/// <summary>
/// Runs each chat's updates in order, but different chats side by side.
///
/// The poll loop used to await every command inline, which meant one slow action —
/// a 60-second shell command, a screenshot on a congested link — stalled the loop
/// that fetches updates. Nothing else got through, and Telegram expired the callback
/// queries of every other tap in the meantime. Handing updates to this instead keeps
/// the loop free, while still guaranteeing that two taps from the same chat never
/// interleave (the second "volume up" must not overtake the first).
/// </summary>
public sealed class UpdateDispatcher : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, Task> _chains = new();
    private readonly object _gate = new();
    private readonly ILogService _log;

    public UpdateDispatcher(ILogService log) => _log = log;

    /// <summary>How many chats currently have work queued or running.</summary>
    public int ActiveChats => _chains.Count;

    /// <summary>Queues work behind whatever this chat already has in flight.</summary>
    public void Enqueue(long chatId, Func<Task> work)
    {
        lock (_gate)
        {
            var previous = _chains.TryGetValue(chatId, out var tail) ? tail : Task.CompletedTask;
            var next = previous.ContinueWith(async _ =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The relay is stopping; nothing to report.
                }
                catch (Exception ex)
                {
                    _log.Error($"Command from chat {chatId} failed", ex);
                }
            }, TaskScheduler.Default).Unwrap();

            _chains[chatId] = next;

            // Drop the entry once the chain drains, so a bot that has talked to many
            // chats over a long uptime does not hold a task per chat forever.
            _ = next.ContinueWith(t =>
            {
                lock (_gate)
                {
                    if (_chains.TryGetValue(chatId, out var current) && ReferenceEquals(current, t))
                        _chains.TryRemove(chatId, out _);
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Waits for everything in flight, up to a deadline.</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_gate)
            pending = _chains.Values.ToArray();
        if (pending.Length == 0)
            return;
        try
        {
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeout)).ConfigureAwait(false);
        }
        catch
        {
            // Individual failures are already logged by the chain itself.
        }
    }

    public async ValueTask DisposeAsync() => await DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
}
