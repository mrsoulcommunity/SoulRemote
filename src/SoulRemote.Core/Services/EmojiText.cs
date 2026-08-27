using System.Text;

namespace SoulRemote.Services;

/// <summary>
/// Finding emoji in a string, and swapping them for Telegram custom ("premium")
/// emoji on the way out.
///
/// Telegram carries a custom emoji as an entity wrapped around an ordinary one:
/// in parse_mode=HTML that is <c>&lt;tg-emoji emoji-id="…"&gt;📸&lt;/tg-emoji&gt;</c>.
/// The wrapped character is not decoration — it is what Telegram shows wherever the
/// custom emoji cannot be drawn (a system notification, a non-premium reader), and
/// the server ignores the entity outright unless it wraps exactly one real emoji.
/// So substitution is a wrap, never a replacement: the original character always
/// survives inside the tag.
///
/// .NET has no Unicode emoji property to ask, so the ranges below are spelled out.
/// They are deliberately generous — every block Telegram treats as emoji — because
/// the cost of including a symbol nobody maps is nothing, while missing one means a
/// character the settings screen offers and the bot then fails to convert.
/// </summary>
public static class EmojiText
{
    /// <summary>Zero-width joiner: glues the parts of ⛹️‍♀️-style sequences together.</summary>
    private const char Zwj = '‍';

    /// <summary>Variation selector 16 — "draw the previous character as emoji".</summary>
    private const char EmojiVariation = '️';

    private const char TextVariation = '︎';

    /// <summary>The combining enclosing keycap, as in 1️⃣.</summary>
    private const char Keycap = '⃣';

    /// <summary>
    /// True when this code point can begin an emoji. Whole blocks rather than a
    /// character list: the exact set moves with every Unicode release, and a range
    /// that admits a few dingbats nobody will ever map is harmless, while a list
    /// that misses one is a bug the user sees.
    /// </summary>
    public static bool IsEmojiStart(int cp) => cp switch
    {
        0x00A9 or 0x00AE => true,                       // © ®
        0x203C or 0x2049 => true,                       // ‼ ⁉
        0x2122 or 0x2139 => true,                       // ™ ℹ
        >= 0x2190 and <= 0x21FF => true,                // arrows: ← ⬅ ↩
        >= 0x2300 and <= 0x23FF => true,                // ⌚ ⌨ ⏱ ⏻ ⏯
        0x24C2 => true,                                 // Ⓜ
        >= 0x25AA and <= 0x25FF => true,                // ▪ ▫ ◽ ◾
        >= 0x2600 and <= 0x27BF => true,                // ☀ ⚡ ⚠ ✅ ❌ ➡
        0x2934 or 0x2935 => true,                       // ⤴ ⤵
        >= 0x2B00 and <= 0x2BFF => true,                // ⬅ ⬆ ⬜ ⭐
        0x3030 or 0x303D or 0x3297 or 0x3299 => true,   // 〰 〽 ㊗ ㊙
        >= 0x1F000 and <= 0x1FAFF => true,              // the emoji planes proper
        _ => false,
    };

    /// <summary>
    /// The length of the emoji beginning at <paramref name="index"/>, or 0 when there
    /// is not one. A single emoji can be several code points — a surrogate pair, a
    /// variation selector, a skin tone, a keycap, or a whole ZWJ sequence — and all of
    /// it has to travel together or the character breaks in half.
    /// </summary>
    public static int SequenceLengthAt(string text, int index)
    {
        if (text is null || index < 0 || index >= text.Length)
            return 0;

        var start = index;
        if (!TryReadEmojiCore(text, ref index))
            return 0;

        // Trailing modifiers, then any number of further ZWJ-joined parts.
        ConsumeModifiers(text, ref index);
        while (index < text.Length && text[index] == Zwj)
        {
            var afterJoiner = index + 1;
            if (!TryReadEmojiCore(text, ref afterJoiner))
                break;
            index = afterJoiner;
            ConsumeModifiers(text, ref index);
        }
        return index - start;
    }

    /// <summary>Reads one emoji code point, advancing past its surrogate pair if it has one.</summary>
    private static bool TryReadEmojiCore(string text, ref int index)
    {
        if (index >= text.Length)
            return false;

        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            if (!IsEmojiStart(char.ConvertToUtf32(text[index], text[index + 1])))
                return false;
            index += 2;
            return true;
        }

        if (!IsEmojiStart(text[index]))
            return false;
        index++;
        return true;
    }

    private static void ConsumeModifiers(string text, ref int index)
    {
        while (index < text.Length)
        {
            var ch = text[index];
            if (ch is EmojiVariation or TextVariation or Keycap)
            {
                index++;
                continue;
            }
            // Skin tones live in the emoji plane, so they arrive as a surrogate pair.
            if (char.IsHighSurrogate(ch) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                && char.ConvertToUtf32(ch, text[index + 1]) is >= 0x1F3FB and <= 0x1F3FF)
            {
                index += 2;
                continue;
            }
            break;
        }
    }

    /// <summary>
    /// Every distinct emoji in a plain (non-markup) string, in the order they first
    /// appear. Used to work out which emoji the bot actually uses.
    /// </summary>
    public static IReadOnlyList<string> Distinct(string? text)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text))
            return found;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < text.Length;)
        {
            var length = SequenceLengthAt(text, i);
            if (length == 0)
            {
                i++;
                continue;
            }
            var value = text.Substring(i, length);
            if (seen.Add(value))
                found.Add(value);
            i += length;
        }
        return found;
    }

    /// <summary>
    /// Wraps every mapped emoji in the outgoing HTML in its custom-emoji tag.
    ///
    /// Three things are stepped over rather than rewritten. Markup, because an emoji
    /// cannot appear inside a tag and a substitution there would corrupt it. The
    /// contents of &lt;pre&gt; and &lt;code&gt;, because those hold shell output and
    /// clipboard text — someone else's data, which the bot has no business editing,
    /// and which Telegram will not render an entity inside anyway. And anything
    /// already inside a tg-emoji tag, which cannot happen on a single pass but is
    /// cheap to be certain of.
    /// </summary>
    public static string ApplyPremium(string? html, IReadOnlyDictionary<string, string>? map)
    {
        if (string.IsNullOrEmpty(html) || map is null || map.Count == 0)
            return html ?? string.Empty;

        var result = new StringBuilder(html.Length + 64);
        var wrapped = false;
        var i = 0;
        while (i < html.Length)
        {
            var ch = html[i];

            if (ch == '<')
            {
                var tagEnd = html.IndexOf('>', i);
                if (tagEnd < 0)
                {
                    // Unterminated markup: copy the rest verbatim rather than guessing.
                    result.Append(html, i, html.Length - i);
                    break;
                }

                var tag = html.Substring(i, tagEnd - i + 1);
                result.Append(tag);
                i = tagEnd + 1;

                if (VerbatimTagName(tag) is { } verbatim)
                {
                    var close = html.IndexOf("</" + verbatim, i, StringComparison.OrdinalIgnoreCase);
                    var stop = close < 0 ? html.Length : close;
                    result.Append(html, i, stop - i);
                    i = stop;
                }
                continue;
            }

            var length = SequenceLengthAt(html, i);
            if (length > 0)
            {
                var emoji = html.Substring(i, length);
                if (map.TryGetValue(emoji, out var id) && IsValidCustomEmojiId(id))
                {
                    result.Append("<tg-emoji emoji-id=\"").Append(id).Append("\">")
                          .Append(emoji).Append("</tg-emoji>");
                    wrapped = true;
                }
                else
                {
                    result.Append(emoji);
                }
                i += length;
                continue;
            }

            result.Append(ch);
            i++;
        }

        // The very same string back when this text had nothing to convert. Callers use
        // that to tell a decorated message from an untouched one without re-scanning.
        return wrapped ? result.ToString() : html;
    }

    /// <summary>The name of a tag whose contents must be copied through untouched, or null.</summary>
    private static string? VerbatimTagName(string tag)
    {
        if (tag.StartsWith("<pre", StringComparison.OrdinalIgnoreCase)) return "pre";
        if (tag.StartsWith("<code", StringComparison.OrdinalIgnoreCase)) return "code";
        return null;
    }

    /// <summary>True when the text already carries at least one custom-emoji tag.</summary>
    public static bool HasPremiumTag(string? html) =>
        html is { Length: > 0 } && html.Contains("<tg-emoji", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a button caption into its leading emoji and the rest — "📸 Capture"
    /// becomes ("📸", "Capture").
    ///
    /// Button labels are the one place Telegram takes no markup at all, so a custom
    /// emoji reaches them through the button's own icon field instead. That field is
    /// drawn <em>before</em> the label, which is exactly where the emoji already is,
    /// so moving it across leaves the button looking the same — only premium.
    /// Anything without a leading emoji comes back unchanged, with an empty first half.
    /// </summary>
    public static (string Emoji, string Label) SplitLeadingEmoji(string? caption)
    {
        if (string.IsNullOrEmpty(caption))
            return (string.Empty, caption ?? string.Empty);

        var length = SequenceLengthAt(caption, 0);
        if (length == 0)
            return (string.Empty, caption);

        var rest = caption[length..].TrimStart();
        // A caption that is nothing but an emoji keeps it: a button with no label at
        // all would be a blank one on any client that will not draw the icon.
        return rest.Length == 0 ? (string.Empty, caption) : (caption[..length], rest);
    }

    /// <summary>
    /// The form of an emoji to compare by, with the variation selectors removed.
    ///
    /// ⚙ and ⚙️ are the same emoji to a reader and different strings to a computer:
    /// the second carries U+FE0F, the invisible "draw this as a picture" request. Our
    /// catalogue holds whichever form the string was typed with, and a sticker pack
    /// holds whichever form its author typed — so matching them literally loses real
    /// matches for a character nobody can see. Only comparison folds; what gets stored
    /// is always the exact character the bot sends, because that is what Telegram
    /// requires the entity to wrap.
    /// </summary>
    public static string Fold(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji))
            return string.Empty;
        if (emoji.IndexOf(EmojiVariation) < 0 && emoji.IndexOf(TextVariation) < 0)
            return emoji;

        var sb = new StringBuilder(emoji.Length);
        foreach (var ch in emoji)
        {
            if (ch is not (EmojiVariation or TextVariation))
                sb.Append(ch);
        }
        return sb.Length == 0 ? emoji : sb.ToString();
    }

    /// <summary>
    /// True when this looks like a custom emoji identifier Telegram will accept.
    ///
    /// The identifier is the sticker document's 64-bit id, sent as a string because a
    /// number that long does not survive a JSON parser. Telegram parses it as a
    /// non-zero int64 and answers "Invalid custom emoji identifier specified" when it
    /// cannot, so the same rule is applied here — an id rejected on this side costs a
    /// message, one rejected on Telegram's side costs a reply.
    /// </summary>
    public static bool IsValidCustomEmojiId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 20)
            return false;
        foreach (var ch in id)
        {
            if (ch is < '0' or > '9')
                return false;
        }
        return ulong.TryParse(id, out var value) && value != 0 && value <= long.MaxValue;
    }
}
