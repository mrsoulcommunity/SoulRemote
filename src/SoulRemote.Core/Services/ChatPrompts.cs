using System.Collections.Concurrent;
using SoulRemote.Abstractions;
using SoulRemote.Localization;

namespace SoulRemote.Services;

/// <summary>A value the bot has asked a chat to supply.</summary>
public enum PromptKind
{
    None,
    Volume,
    KillProcess,
    Clipboard,
    TypeText,
    Speak,
    OpenLink,
    ShellCommand,

    /// <summary>A folder to list or a file to send back.</summary>
    Path,

    /// <summary>A file to fetch from this PC and send to the chat.</summary>
    GetFile,
}

/// <summary>
/// Tracks the one outstanding question per chat, so a button that needs a value
/// ("Type text…") can collect the next message as its argument. Prompts expire so
/// a forgotten tap never silently swallows a later message.
/// </summary>
public sealed class ChatPrompts
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<long, (PromptKind Kind, DateTime AskedAt)> _pending = new();
    private readonly IClock _clock;

    public ChatPrompts(IClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    public void Ask(long chatId, PromptKind kind) => _pending[chatId] = (kind, _clock.UtcNow);

    public void Clear(long chatId) => _pending.TryRemove(chatId, out _);

    /// <summary>Takes and clears the pending prompt, or None if there is none or it expired.</summary>
    public PromptKind Take(long chatId)
    {
        if (!_pending.TryRemove(chatId, out var entry))
            return PromptKind.None;
        return _clock.UtcNow - entry.AskedAt > Lifetime ? PromptKind.None : entry.Kind;
    }

    public bool HasPending(long chatId) =>
        _pending.TryGetValue(chatId, out var e) && _clock.UtcNow - e.AskedAt <= Lifetime;

    public static string PromptFor(PromptKind kind) => Strings.Get(kind switch
    {
        PromptKind.Volume => "bot.prompt.volume",
        PromptKind.KillProcess => "bot.prompt.kill",
        PromptKind.Clipboard => "bot.prompt.clip",
        PromptKind.TypeText => "bot.prompt.type",
        PromptKind.Speak => "bot.prompt.speak",
        PromptKind.OpenLink => "bot.prompt.open",
        PromptKind.ShellCommand => "bot.prompt.shell",
        PromptKind.Path or PromptKind.GetFile => "bot.prompt.path",
        _ => "bot.prompt.generic",
    });

    public static string PlaceholderFor(PromptKind kind) => Strings.Get(kind switch
    {
        PromptKind.Volume => "bot.placeholder.volume",
        PromptKind.KillProcess => "bot.placeholder.kill",
        PromptKind.OpenLink => "bot.placeholder.open",
        PromptKind.ShellCommand => "bot.placeholder.shell",
        PromptKind.Path or PromptKind.GetFile => "bot.placeholder.path",
        _ => "bot.placeholder.generic",
    });
}
