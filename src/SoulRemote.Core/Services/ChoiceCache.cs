using System.Collections.Concurrent;

namespace SoulRemote.Services;

/// <summary>
/// Remembers the list a chat is currently looking at, so a button can say "the
/// third one" instead of carrying the value itself.
///
/// Telegram caps callback_data at 64 bytes, and most of what the settings screens
/// offer fits inside that — a chat id is 20 characters, a power-plan GUID is 36.
/// A Wi-Fi profile name does not: SSIDs are up to 32 characters of arbitrary
/// Unicode, which is 128 bytes of UTF-8 before the prefix. Those get an index, and
/// this is what the index means.
///
/// The same trade-off <see cref="FileBrowser"/> makes for directory entries, and
/// with the same consequence: a panel left open across a restart points at a list
/// that is no longer here, so <see cref="Get"/> answers null rather than guessing
/// and the caller says the screen is stale.
/// </summary>
public sealed class ChoiceCache
{
    private readonly ConcurrentDictionary<long, IReadOnlyList<string>> _lists = new();

    public void Put(long chatId, IReadOnlyList<string> items) => _lists[chatId] = items;

    /// <summary>The item at that index, or null when the list is gone or the index is not in it.</summary>
    public string? Get(long chatId, int index)
    {
        if (!_lists.TryGetValue(chatId, out var items))
            return null;
        return index >= 0 && index < items.Count ? items[index] : null;
    }

    public void Forget(long chatId) => _lists.TryRemove(chatId, out _);
}
