using SoulRemote.Models;

namespace SoulRemote.Services;

/// <summary>What we have learned about this bot's right to send custom emoji.</summary>
public enum PremiumEmojiState
{
    /// <summary>Nothing sent yet, or nothing mapped to send.</summary>
    Unknown,

    /// <summary>A custom emoji went out and came back intact — Telegram is honouring them.</summary>
    Working,

    /// <summary>Telegram dropped or refused them. The bot is not entitled to send custom emoji.</summary>
    Refused,
}

/// <summary>
/// Dresses outgoing Telegram content in the user's custom emoji.
/// </summary>
public interface IEmojiStyler
{
    /// <summary>True when there is a mapping to apply and the user wants it applied.</summary>
    bool IsActive { get; }

    PremiumEmojiState State { get; }

    /// <summary>Raised when the state changes, so a settings screen can say so.</summary>
    event Action<PremiumEmojiState>? StateChanged;

    /// <summary>Wraps mapped emoji in the HTML of a message, caption or panel.</summary>
    string Decorate(string? html);

    /// <summary>
    /// Moves mapped emoji off button labels and into the buttons' icon field. Returns
    /// the markup unchanged — the very same object — when there is nothing to do.
    /// </summary>
    object? DecorateMarkup(object? replyMarkup);

    /// <summary>
    /// Learns from what Telegram did with a message we decorated: an entity that
    /// survived the round trip proves the bot may send custom emoji, and one that was
    /// stripped proves it may not.
    /// </summary>
    void Observe(string sentHtml, TgMessage? echoed);

    /// <summary>Records that Telegram refused a call outright because of a custom emoji.</summary>
    void ReportRejected(string? description);
}

/// <summary>
/// The live custom-emoji look, applied on the way out of the app.
///
/// Everything happens at one choke point rather than at each of the hundred places
/// the bot composes a string: a screen, a report, a toast and a caption are all
/// eventually one call to the Telegram client, and decorating there is the only
/// version of this that cannot miss a case. It also means the plain text is still
/// in hand when Telegram refuses the decorated one, which is what lets a bad
/// mapping cost nothing worse than one retry.
///
/// Telegram is not obliged to honour custom emoji from every bot, and — this is the
/// awkward part — it does not say so. A bot without the entitlement gets 200 OK and
/// a message whose entities were quietly dropped. So the entitlement is learned by
/// looking: the echoed message that comes back from every send is checked for the
/// entity we put in, and buttons are only rewritten once one has survived.
/// </summary>
public sealed class PremiumEmojiStyler : IEmojiStyler
{
    private readonly ILogService _log;

    private readonly object _gate = new();
    private IReadOnlyDictionary<string, string> _map = EmptyMap;
    private bool _enabled;
    private PremiumEmojiState _state = PremiumEmojiState.Unknown;

    private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.Ordinal);

    public event Action<PremiumEmojiState>? StateChanged;

    public PremiumEmojiStyler(ISettingsService settings, ILogService log)
    {
        _log = log;
        Reload(settings.Current);
        settings.Changed += Reload;
    }

    public PremiumEmojiState State
    {
        get { lock (_gate) return _state; }
    }

    public bool IsActive
    {
        get { lock (_gate) return _enabled && _map.Count > 0 && _state != PremiumEmojiState.Refused; }
    }

    /// <summary>How many of the bot's emoji currently have a premium stand-in.</summary>
    public int MappedCount
    {
        get { lock (_gate) return _map.Count; }
    }

    private void Reload(AppSettings settings)
    {
        lock (_gate)
        {
            _enabled = settings.UsePremiumEmoji;
            _map = settings.PremiumEmoji.Count == 0
                ? EmptyMap
                : new Dictionary<string, string>(settings.PremiumEmoji, StringComparer.Ordinal);

            // A refusal belongs to a bot's entitlement, not to the map. But a user who
            // has just changed the mapping deserves the question asked again rather
            // than being told forever that something they have since fixed is broken.
            if (_state == PremiumEmojiState.Refused)
                _state = PremiumEmojiState.Unknown;
        }
    }

    public string Decorate(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return html ?? string.Empty;

        IReadOnlyDictionary<string, string> map;
        lock (_gate)
        {
            // Nothing more is sent once Telegram has said no. Both of its ways of
            // saying so cost something to ignore: an entitlement it will not grant
            // makes every message carry markup that is thrown away, and an identifier
            // it will not parse makes every message a 400 followed by a second send.
            // The verdict is dropped again the moment the mapping changes, so a user
            // who fixes it is tested afresh rather than told no forever.
            if (!_enabled || _map.Count == 0 || _state == PremiumEmojiState.Refused)
                return html;
            map = _map;
        }
        return EmojiText.ApplyPremium(html, map);
    }

    public object? DecorateMarkup(object? replyMarkup)
    {
        if (replyMarkup is null)
            return null;

        IReadOnlyDictionary<string, string> map;
        lock (_gate)
        {
            // Buttons wait for proof. Decorating text is free either way — a stripped
            // entity still leaves the ordinary emoji standing — but a button gives its
            // emoji up to the icon field, and if Telegram then drops the icon the
            // button has lost the emoji altogether. So labels are only rewritten once
            // a custom emoji has been seen to survive.
            if (!_enabled || _map.Count == 0 || _state != PremiumEmojiState.Working)
                return replyMarkup;
            map = _map;
        }

        return replyMarkup switch
        {
            TgInlineKeyboardMarkup inline => DecorateInline(inline, map),
            TgReplyKeyboardMarkup bar => DecorateBar(bar, map),
            _ => replyMarkup,
        };
    }

    private static TgInlineKeyboardMarkup DecorateInline(
        TgInlineKeyboardMarkup markup, IReadOnlyDictionary<string, string> map)
    {
        var rows = new List<List<TgInlineKeyboardButton>>(markup.InlineKeyboard.Count);
        var changed = false;

        foreach (var row in markup.InlineKeyboard)
        {
            var copied = new List<TgInlineKeyboardButton>(row.Count);
            foreach (var button in row)
            {
                var (icon, label) = IconFor(button.Text, map);
                if (icon is null)
                {
                    copied.Add(button);
                    continue;
                }
                changed = true;
                copied.Add(new TgInlineKeyboardButton
                {
                    Text = label,
                    CallbackData = button.CallbackData,
                    Url = button.Url,
                    IconCustomEmojiId = icon,
                });
            }
            rows.Add(copied);
        }

        // Handing back the original when nothing moved keeps the "message is not
        // modified" path working: an edit that rebuilt an identical keyboard as a new
        // object still compares equal to Telegram, but there is no reason to allocate.
        return changed ? new TgInlineKeyboardMarkup { InlineKeyboard = rows } : markup;
    }

    private static TgReplyKeyboardMarkup DecorateBar(
        TgReplyKeyboardMarkup markup, IReadOnlyDictionary<string, string> map)
    {
        var rows = new List<List<TgKeyboardButton>>(markup.Keyboard.Count);
        var changed = false;

        foreach (var row in markup.Keyboard)
        {
            var copied = new List<TgKeyboardButton>(row.Count);
            foreach (var button in row)
            {
                var (icon, label) = IconFor(button.Text, map);
                if (icon is null)
                {
                    copied.Add(button);
                    continue;
                }
                changed = true;
                copied.Add(new TgKeyboardButton { Text = label, IconCustomEmojiId = icon });
            }
            rows.Add(copied);
        }

        if (!changed)
            return markup;

        return new TgReplyKeyboardMarkup
        {
            Keyboard = rows,
            ResizeKeyboard = markup.ResizeKeyboard,
            IsPersistent = markup.IsPersistent,
            Placeholder = markup.Placeholder,
        };
    }

    /// <summary>
    /// The icon and remaining label for a button caption, or a null icon when its
    /// leading emoji is not one the user has mapped.
    ///
    /// Only the emoji moves; the words stay. That matters most for the shortcut bar,
    /// whose captions come back to the bot as ordinary messages and are recognised by
    /// string — which is why the router matches those captions with their leading
    /// emoji stripped too, so a bar wearing premium icons still works.
    /// </summary>
    private static (string? Icon, string Label) IconFor(string caption, IReadOnlyDictionary<string, string> map)
    {
        var (emoji, rest) = EmojiText.SplitLeadingEmoji(caption);
        if (emoji.Length == 0)
            return (null, caption);
        return map.TryGetValue(emoji, out var id) && EmojiText.IsValidCustomEmojiId(id)
            ? (id, rest)
            : (null, caption);
    }

    public void Observe(string sentHtml, TgMessage? echoed)
    {
        if (!EmojiText.HasPremiumTag(sentHtml) || echoed is null)
            return;

        var survived = echoed.Entities?.Any(e => e.IsCustomEmoji) == true;
        Move(survived ? PremiumEmojiState.Working : PremiumEmojiState.Refused,
            survived
                ? "Telegram is honouring this bot's premium emoji."
                : "Telegram stripped the premium emoji from that message. This bot may not send them: "
                  + "its owner needs a Telegram Premium subscription, or the bot needs an extra username bought on Fragment.");
    }

    public void ReportRejected(string? description)
    {
        Move(PremiumEmojiState.Refused,
            $"Telegram refused a message because of a premium emoji ({description}). Falling back to plain emoji.");
    }

    private void Move(PremiumEmojiState next, string message)
    {
        lock (_gate)
        {
            if (_state == next)
                return;
            _state = next;
        }

        if (next == PremiumEmojiState.Refused)
            _log.Warning(message);
        else
            _log.Info(message);
        StateChanged?.Invoke(next);
    }
}

/// <summary>
/// Turns a Telegram emoji pack into a mapping for the bot's own emoji.
///
/// Matching is by the emoji a custom one stands for, and it has to be: Telegram
/// ignores a custom emoji entity unless it wraps exactly that character. So the
/// question a pack answers is never "which of these do I like" but "which of the
/// bot's emoji does this pack have a version of" — which is also what makes
/// converting the whole bot a single action instead of forty.
/// </summary>
public static class EmojiPack
{
    /// <summary>The link Telegram uses for an emoji pack.</summary>
    private const string AddEmojiPath = "addemoji/";

    /// <summary>
    /// Which of the bot's emoji this pack can stand in for. Keys are the bot's own
    /// characters, exactly as it sends them.
    /// </summary>
    public static Dictionary<string, string> Match(IEnumerable<TgSticker>? stickers)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stickers is null)
            return result;

        // The pack is indexed by folded emoji first, so each of the bot's emoji is a
        // single lookup and the first sticker for a character wins — packs routinely
        // carry several takes on the same one, and picking any later one would make
        // the same import produce a different result each time.
        var byEmoji = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sticker in stickers)
        {
            if (sticker?.CustomEmojiId is not { Length: > 0 } id || !EmojiText.IsValidCustomEmojiId(id))
                continue;
            if (sticker.Emoji is not { Length: > 0 } emoji)
                continue;
            byEmoji.TryAdd(EmojiText.Fold(emoji), id);
        }

        foreach (var use in EmojiCatalog.All)
        {
            if (byEmoji.TryGetValue(EmojiText.Fold(use.Emoji), out var id))
                result[use.Emoji] = id;
        }
        return result;
    }

    /// <summary>
    /// The pack name inside whatever the user sent: a bare name, a t.me/addemoji
    /// link, or a tg://addemoji?set= one. Returns null when there is no name in it.
    /// </summary>
    public static string? NameFrom(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return null;

        var marker = text.IndexOf(AddEmojiPath, StringComparison.OrdinalIgnoreCase);
        var fromLink = marker >= 0;
        if (fromLink)
        {
            text = text[(marker + AddEmojiPath.Length)..];
        }
        else if (text.Contains("addemoji", StringComparison.OrdinalIgnoreCase)
                 && text.IndexOf("set=", StringComparison.OrdinalIgnoreCase) is var q && q >= 0)
        {
            text = text[(q + 4)..];
            fromLink = true;
        }

        // Only a link has anything after the name worth discarding. A bare word stands
        // or falls as it was typed: cutting it at the first space would turn a sentence
        // somebody meant as a question into a pack name that half looks right.
        if (fromLink)
        {
            var end = text.IndexOfAny(new[] { '?', '&', '#', '/', ' ', '\n', '\r', '\t' });
            if (end >= 0)
                text = text[..end];
        }

        // Telegram set names are the same shape as usernames.
        if (text.Length == 0 || text.Length > 64)
            return null;
        foreach (var ch in text)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
                return null;
        }
        return text;
    }
}
