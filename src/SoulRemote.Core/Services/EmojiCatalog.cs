using System.Text;
using SoulRemote.Localization;

namespace SoulRemote.Services;

/// <summary>One emoji the bot uses, and where the user meets it.</summary>
/// <param name="Emoji">The character itself — also the key it is stored under.</param>
/// <param name="Uses">How many catalogue rows and code literals it appears in.</param>
public readonly record struct EmojiUse(string Emoji, int Uses);

/// <summary>
/// Every emoji the Telegram bot can put in front of a user.
///
/// Read out of the string catalogue rather than typed out again here. The bot's
/// emoji live inside its strings — "📸 Capture", "⚠️ …" — and a hand-kept second
/// list would start complete and then quietly stop being so, one new string at a
/// time, leaving a settings screen that promises to convert every emoji and misses
/// the ones added last month. Scanning the table means the screen is right by
/// construction: add a string with a new emoji and it is on the list.
///
/// Both language halves are scanned, because a row is free to use a different
/// emoji in Persian, and both are things a user will see.
/// </summary>
public static class EmojiCatalog
{
    /// <summary>
    /// Catalogue keys whose text the bot sends. Desktop-only rows (<c>ui.</c>,
    /// <c>err.</c>) are left out: converting an emoji that only ever appears inside
    /// a WPF window would be a setting with no effect.
    /// </summary>
    private static readonly string[] BotKeyPrefixes = { "bot.", "act.", "sys." };

    /// <summary>
    /// The emoji whose name is given rather than worked out.
    ///
    /// Two kinds end up here. Some the bot composes in code and never stores as a
    /// string — the toggle marks, the row bullets, the home panel's own badge — so
    /// there is no row to find them in. The rest do appear in strings, but only ever
    /// inside a sentence: deriving a name from "Too many tries. Wait a moment" gives
    /// exactly that, which is a line of prose where a label was wanted. Naming those
    /// few by hand costs one catalogue row each and is what makes the list scannable.
    /// </summary>
    private static readonly (string Emoji, string LabelKey)[] Named =
    {
        ("🎛", "bot.emoji.label.panel"),
        ("👤", "bot.set.chat.title"),
        ("✅", "bot.emoji.label.on"),
        ("⬜", "bot.emoji.label.off"),
        ("▫️", "bot.set.plan.title"),
        ("📶", "bot.set.wifi.title"),
        ("⚠️", "bot.emoji.label.warning"),
        ("⏱", "bot.emoji.label.uptime"),
        ("📎", "bot.emoji.label.attachment"),
        ("👋", "bot.emoji.label.welcome"),
        ("⏳", "bot.emoji.label.waiting"),
        ("🟢", "bot.emoji.label.online"),
        ("⛔", "bot.emoji.label.blocked"),
        ("🛰", "bot.emoji.label.relay"),
        ("🔊", "bot.emoji.label.volume"),
        ("🔇", "bot.emoji.label.muted"),
    };

    /// <summary>
    /// Every emoji the bot uses, most-used first. Built once and then shared: the
    /// string catalogue is a compile-time constant, so the answer cannot change while
    /// the app is running, and the bot's poll thread reads this list at the same time
    /// as the window does.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<EmojiUse>> Catalogue =
        new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<EmojiUse> All => Catalogue.Value;

    public static int Count => All.Count;

    /// <summary>
    /// How many of the bot's emoji a mapping actually converts.
    ///
    /// Not simply the size of the mapping. A settings file can hold an entry for an
    /// emoji this build no longer uses — they are kept rather than deleted, so that a
    /// release which rewords one string does not quietly destroy part of an imported
    /// pack — but such an entry converts nothing, and counting it would produce the
    /// nonsense of "68 of 67 converted".
    /// </summary>
    public static int ConvertedCount(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0)
            return 0;
        var n = 0;
        foreach (var use in All)
        {
            if (map.ContainsKey(use.Emoji))
                n++;
        }
        return n;
    }

    private static IReadOnlyList<EmojiUse> Build()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();

        void Note(string emoji)
        {
            if (counts.TryGetValue(emoji, out var n))
            {
                counts[emoji] = n + 1;
                return;
            }
            counts[emoji] = 1;
            order.Add(emoji);
        }

        foreach (var key in Keys())
        {
            foreach (var language in Enum.GetValues<AppLanguage>())
            {
                foreach (var emoji in EmojiText.Distinct(Strings.Get(language, key)))
                    Note(emoji);
            }
        }

        foreach (var (emoji, _) in Named)
            Note(emoji);

        // Most-used first, then first-seen: the emoji someone is most likely to want
        // to convert are the ones the bot shows on every screen, and the order has to
        // be the same every time or the list would reshuffle under the user's finger.
        return order
            .Select(e => new EmojiUse(e, counts[e]))
            .OrderByDescending(e => e.Uses)
            .ThenBy(e => order.IndexOf(e.Emoji))
            .ToArray();
    }

    /// <summary>The catalogue keys the bot sends, in catalogue order.</summary>
    private static IEnumerable<string> Keys() =>
        Strings.Keys.Where(k => BotKeyPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>
    /// A short name for an emoji, taken from the first string it appears in with the
    /// markup, placeholders and emoji stripped out — "📸 &lt;b&gt;Capture&lt;/b&gt;"
    /// gives "Capture". Derived rather than written down for the same reason the list
    /// itself is: a name typed here would be a second thing to keep in step, and it
    /// would only ever be wrong in the language nobody testing it reads.
    /// </summary>
    public static string LabelFor(string emoji, AppLanguage language)
    {
        // A name given by hand always wins. It was written for exactly this list, and
        // the emoji it was written for are the ones no row describes well.
        foreach (var (candidate, labelKey) in Named)
        {
            if (!string.Equals(candidate, emoji, StringComparison.Ordinal))
                continue;
            // Held to the same floor as a derived name. A key that reduces to nothing —
            // or to a lone "%" — is a mistake in the table above, and falling through
            // to the derived name is a better answer than printing the mistake.
            var named = Clean(Strings.Get(language, labelKey));
            if (named.Length >= 2)
                return named;
        }

        var best = string.Empty;
        var bestScore = (int.MaxValue, int.MaxValue);

        foreach (var key in Keys())
        {
            var text = Strings.Get(language, key);
            if (!text.Contains(emoji, StringComparison.Ordinal))
                continue;

            var label = Clean(text);
            // One character is a leftover, not a name. Two can be a whole Persian word
            // — "کم" for the volume-down button — so the floor stops at two.
            if (label.Length < 2)
                continue;

            var score = (Rank(key), label.Length);
            if (score.CompareTo(bestScore) >= 0)
                continue;
            bestScore = score;
            best = label;
        }

        return best.Length > 0 ? best : emoji;
    }

    /// <summary>
    /// How good a name a row is likely to give. A menu caption or a screen title is a
    /// short noun for the thing the emoji marks — "Capture" — while an ordinary line
    /// is a sentence that happens to start with it, and reduces to something like
    /// "Screenshot failed". Both describe the same emoji; only one of them reads as a
    /// label in a list of sixty.
    /// </summary>
    private static int Rank(string key) =>
        key.Contains(".menu.", StringComparison.Ordinal) || key.Contains(".bar.", StringComparison.Ordinal) ? 0
        : key.EndsWith(".title", StringComparison.Ordinal) ? 1
        : 2;

    public static string LabelFor(string emoji) => LabelFor(emoji, Strings.Current);

    /// <summary>Strips tags, placeholders and emoji, leaving the words.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var ch = text[i];
            if (ch == '<')
            {
                var end = text.IndexOf('>', i);
                if (end < 0)
                    break;
                i = end + 1;
                continue;
            }
            if (ch == '{')
            {
                var end = text.IndexOf('}', i);
                if (end >= 0)
                {
                    i = end + 1;
                    continue;
                }
            }
            var length = EmojiText.SequenceLengthAt(text, i);
            if (length > 0)
            {
                i += length;
                continue;
            }
            sb.Append(ch);
            i++;
        }

        // What stripping leaves behind is runs of spaces and punctuation that used to
        // sit against a placeholder: "Connected to {0}. Use…" becomes "Connected to .
        // Use…" unless the gap is closed up again.
        var collapsed = new StringBuilder(sb.Length);
        var lastWasSpace = false;
        foreach (var ch in sb.ToString())
        {
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace && lastWasSpace)
                continue;
            if (lastWasSpace && ch is '.' or ',' or ':' or ';' or '%' or '،' or '؛' && collapsed.Length > 0)
                collapsed.Length--;
            collapsed.Append(isSpace ? ' ' : ch);
            lastWasSpace = isSpace;
        }

        var label = collapsed.ToString().Trim(' ', '—', '·', ':', '.', '،', '؛', '%');

        // A unit left stranded by its number — the "s" of "{0}s", the "٪" of "{0}٪" —
        // reads as a typo rather than as part of a name.
        var lastSpace = label.LastIndexOf(' ');
        if (lastSpace > 0 && label.Length - lastSpace == 2)
            label = label[..lastSpace].TrimEnd(' ', ':', ',', '،');

        // The catalogue is written for Telegram's HTML parser, so an ampersand in a
        // string is stored escaped. A label is plain text and has to read as one.
        label = label.Replace("&amp;", "&", StringComparison.Ordinal)
                     .Replace("&lt;", "<", StringComparison.Ordinal)
                     .Replace("&gt;", ">", StringComparison.Ordinal);

        return TextUtil.Clip(label, 32);
    }
}
