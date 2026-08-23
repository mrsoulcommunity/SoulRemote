using System.Globalization;
using System.Text;

namespace SoulRemote.Services;

/// <summary>Formatting helpers shared across the bot command layer.</summary>
public static class TextUtil
{
    /// <summary>Escapes text for Telegram parse_mode=HTML.</summary>
    public static string Html(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>Wraps text in a Telegram &lt;pre&gt; block (content escaped).</summary>
    public static string Pre(string? text) => $"<pre>{Html(text)}</pre>";

    public static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unit]);
    }

    public static string HumanDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1)
            return $"{span.Hours}h {span.Minutes}m";
        return $"{span.Minutes}m {span.Seconds}s";
    }

    /// <summary>
    /// Splits an HTML-formatted message into pieces Telegram will accept.
    ///
    /// Telegram rejects a message whose markup does not parse, so a naive substring
    /// is dangerous in two ways: it can cut a tag in half ("&lt;b" / "&gt;bold"), and
    /// it can cut an entity in half ("&amp;a" / "mp;"). Either turns the whole reply
    /// into a 400 rather than a truncated one. So the split point walks back to a
    /// newline, then a space, then — as a last resort — to the nearest position that
    /// is outside both a tag and an entity.
    ///
    /// Balancing tags <em>across</em> pieces is deliberately not attempted: every
    /// message this app formats keeps its markup within a single line, and a splitter
    /// that rewrote markup would be guessing.
    /// </summary>
    public static IReadOnlyList<string> SplitForTelegram(string? text, int limit = 4000)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        if (limit < 16)
            limit = 16;
        if (text.Length <= limit)
            return new[] { text };

        var pieces = new List<string>();
        var pos = 0;
        while (pos < text.Length)
        {
            var remaining = text.Length - pos;
            if (remaining <= limit)
            {
                pieces.Add(text[pos..]);
                break;
            }

            var take = SafeBreak(text, pos, limit);
            pieces.Add(text.Substring(pos, take));
            pos += take;
        }
        return pieces;
    }

    /// <summary>
    /// How many characters may be taken from <paramref name="start"/> without landing
    /// inside a tag or an entity. Always returns at least 1, so the loop terminates
    /// even on pathological input such as a single 10,000-character "tag".
    /// </summary>
    private static int SafeBreak(string text, int start, int limit)
    {
        var hardEnd = start + limit;

        // Prefer a line break: our messages are line-structured, so this keeps markup intact.
        var nl = text.LastIndexOf('\n', hardEnd - 1, limit);
        if (nl > start && IsSafeBoundary(text, start, nl + 1))
            return nl + 1 - start;

        var space = text.LastIndexOf(' ', hardEnd - 1, limit);
        if (space > start && IsSafeBoundary(text, start, space + 1))
            return space + 1 - start;

        // Walk back to the first boundary that is outside a tag and an entity.
        for (var cut = hardEnd; cut > start; cut--)
        {
            if (IsSafeBoundary(text, start, cut))
                return cut - start;
        }
        return Math.Min(limit, text.Length - start);
    }

    /// <summary>True when cutting immediately before <paramref name="cut"/> splits nothing.</summary>
    private static bool IsSafeBoundary(string text, int start, int cut)
    {
        var openTag = -1;
        var openEntity = -1;
        for (var i = start; i < cut; i++)
        {
            switch (text[i])
            {
                case '<':
                    openTag = i;
                    break;
                case '>':
                    openTag = -1;
                    break;
                case '&':
                    openEntity = i;
                    break;
                case ';':
                    openEntity = -1;
                    break;
                default:
                    // An entity reference is short; anything longer was a bare ampersand.
                    if (openEntity >= 0 && i - openEntity > 10)
                        openEntity = -1;
                    break;
            }
        }
        return openTag < 0 && openEntity < 0;
    }

    /// <summary>
    /// Trims to a character budget on a whole-character boundary, appending an ellipsis
    /// when anything was dropped. Used for callback toasts and photo captions, both of
    /// which Telegram truncates far more bluntly.
    /// </summary>
    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
            return string.Empty;
        if (text.Length <= max)
            return text;
        if (max <= 1)
            return text[..max];

        var cut = max - 1;
        // Never leave half a surrogate pair behind.
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + "…";
    }

    /// <summary>Formats a byte count of a file for a user-facing message.</summary>
    public static string FileSize(long bytes) => HumanBytes(bytes);

    /// <summary>Builds "label: value" report lines with the label in bold.</summary>
    public static void AppendField(StringBuilder sb, string label, string value)
        => sb.AppendLine($"<b>{Html(label)}:</b> {value}");
}
