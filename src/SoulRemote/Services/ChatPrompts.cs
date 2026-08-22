using System.Collections.Concurrent;

namespace SoulRemote.Services;

/// <summary>A value the bot has asked a chat to supply.</summary>
public enum PromptKind { None, Volume, KillProcess, Clipboard, TypeText, Speak, OpenLink, ShellCommand }

/// <summary>
/// Tracks the one outstanding question per chat, so a button that needs a value
/// ("Type text…") can collect the next message as its argument. Prompts expire so
/// a forgotten tap never silently swallows a later message.
/// </summary>
public sealed class ChatPrompts
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<long, (PromptKind Kind, DateTime AskedAt)> _pending = new();

    public void Ask(long chatId, PromptKind kind) => _pending[chatId] = (kind, DateTime.UtcNow);

    public void Clear(long chatId) => _pending.TryRemove(chatId, out _);

    /// <summary>Takes and clears the pending prompt, or None if there is none or it expired.</summary>
    public PromptKind Take(long chatId)
    {
        if (!_pending.TryRemove(chatId, out var entry))
            return PromptKind.None;
        return DateTime.UtcNow - entry.AskedAt > Lifetime ? PromptKind.None : entry.Kind;
    }

    public bool HasPending(long chatId) =>
        _pending.TryGetValue(chatId, out var e) && DateTime.UtcNow - e.AskedAt <= Lifetime;

    public static string PromptFor(PromptKind kind) => kind switch
    {
        PromptKind.Volume => "Send a level from <b>0</b> to <b>100</b>.",
        PromptKind.KillProcess => "Send the process <b>name</b> or <b>PID</b> to end.",
        PromptKind.Clipboard => "Send the text to put on the clipboard.",
        PromptKind.TypeText => "Send the text to type into the focused window.",
        PromptKind.Speak => "Send the text to speak aloud.",
        PromptKind.OpenLink => "Send a web link to open. Files and folders need file access switched on.",
        PromptKind.ShellCommand => "Send the command to run.",
        _ => "Send a value.",
    };

    public static string PlaceholderFor(PromptKind kind) => kind switch
    {
        PromptKind.Volume => "0-100",
        PromptKind.KillProcess => "chrome or 1234",
        PromptKind.OpenLink => "https://…",
        PromptKind.ShellCommand => "ipconfig /all",
        _ => "Type here",
    };
}
