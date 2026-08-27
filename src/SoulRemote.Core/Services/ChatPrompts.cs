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

    /// <summary>Seconds to hold a Telegram long-poll open for.</summary>
    PollTimeout,

    /// <summary>Days to keep log files before they are swept.</summary>
    LogRetention,

    /// <summary>Where files sent to the bot are written.</summary>
    DownloadFolder,

    /// <summary>Panel brightness, 0-100.</summary>
    Brightness,

    /// <summary>A friendlier label for a paired chat. Carries the chat id as its argument.</summary>
    RenameChat,

    /// <summary>An emoji pack to convert the whole bot with — a link, a name, or one emoji from it.</summary>
    EmojiPack,

    /// <summary>Premium emoji to adopt, each standing in for the ordinary one it is a version of.</summary>
    PremiumEmoji,

    /// <summary>
    /// The premium version of one particular emoji. Carries that emoji's index in the
    /// catalogue as its argument, because the answer arrives as a plain message with
    /// nothing to say which button asked for it.
    /// </summary>
    PremiumEmojiFor,
}

/// <summary>
/// Tracks the one outstanding question per chat, so a button that needs a value
/// ("Type text…") can collect the next message as its argument. Prompts expire so
/// a forgotten tap never silently swallows a later message.
/// </summary>
public sealed class ChatPrompts
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<long, (PromptKind Kind, string? Arg, DateTime AskedAt)> _pending = new();
    private readonly IClock _clock;

    public ChatPrompts(IClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    /// <param name="arg">
    /// What the answer will be applied to, for the kinds that need a target — the
    /// chat id being renamed. Carried here rather than in the callback payload of
    /// the reply, because the answer arrives as an ordinary message with no payload.
    /// </param>
    public void Ask(long chatId, PromptKind kind, string? arg = null)
        => _pending[chatId] = (kind, arg, _clock.UtcNow);

    public void Clear(long chatId) => _pending.TryRemove(chatId, out _);

    /// <summary>Takes and clears the pending prompt, or None if there is none or it expired.</summary>
    public PromptKind Take(long chatId) => Take(chatId, out _);

    /// <summary>The same, also handing back what the prompt was aimed at.</summary>
    public PromptKind Take(long chatId, out string? arg)
    {
        arg = null;
        if (!_pending.TryRemove(chatId, out var entry))
            return PromptKind.None;
        if (_clock.UtcNow - entry.AskedAt > Lifetime)
            return PromptKind.None;
        arg = entry.Arg;
        return entry.Kind;
    }

    public bool HasPending(long chatId) =>
        _pending.TryGetValue(chatId, out var e) && _clock.UtcNow - e.AskedAt <= Lifetime;

    /// <summary>The kinds that write settings, and so answer to the remote-settings switch.</summary>
    public static bool IsSettingsPrompt(PromptKind kind) => kind
        is PromptKind.PollTimeout or PromptKind.LogRetention or PromptKind.DownloadFolder
        or PromptKind.Brightness or PromptKind.RenameChat
        or PromptKind.EmojiPack or PromptKind.PremiumEmoji or PromptKind.PremiumEmojiFor;

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
        PromptKind.PollTimeout => "bot.prompt.poll",
        PromptKind.LogRetention => "bot.prompt.logdays",
        PromptKind.DownloadFolder => "bot.prompt.folder",
        PromptKind.Brightness => "bot.prompt.brightness",
        PromptKind.RenameChat => "bot.prompt.rename",
        PromptKind.EmojiPack => "bot.prompt.emojipack",
        PromptKind.PremiumEmoji => "bot.prompt.premiumemoji",
        PromptKind.PremiumEmojiFor => "bot.prompt.premiumemojifor",
        _ => "bot.prompt.generic",
    });

    public static string PlaceholderFor(PromptKind kind) => Strings.Get(kind switch
    {
        PromptKind.Volume => "bot.placeholder.volume",
        PromptKind.KillProcess => "bot.placeholder.kill",
        PromptKind.OpenLink => "bot.placeholder.open",
        PromptKind.ShellCommand => "bot.placeholder.shell",
        PromptKind.Path or PromptKind.GetFile => "bot.placeholder.path",
        PromptKind.PollTimeout => "bot.placeholder.poll",
        PromptKind.LogRetention => "bot.placeholder.logdays",
        PromptKind.DownloadFolder => "bot.placeholder.folder",
        PromptKind.Brightness => "bot.placeholder.brightness",
        PromptKind.RenameChat => "bot.placeholder.rename",
        PromptKind.EmojiPack => "bot.placeholder.emojipack",
        PromptKind.PremiumEmoji or PromptKind.PremiumEmojiFor => "bot.placeholder.premiumemoji",
        _ => "bot.placeholder.generic",
    });
}
