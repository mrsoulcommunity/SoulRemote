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
        return $"{value:0.##} {units[unit]}";
    }

    public static string HumanDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1)
            return $"{span.Hours}h {span.Minutes}m";
        return $"{span.Minutes}m {span.Seconds}s";
    }
}
