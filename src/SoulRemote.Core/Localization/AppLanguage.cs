namespace SoulRemote.Localization;

/// <summary>
/// The languages Soul Remote speaks. Persian is first-class rather than an
/// afterthought: the app exists because Telegram is blocked in Iran, so most of
/// the people running it read Persian more comfortably than English.
/// </summary>
public enum AppLanguage
{
    English,
    Persian,
}

public static class AppLanguageExtensions
{
    /// <summary>The two-letter tag stored in settings and shown on the language button.</summary>
    public static string Tag(this AppLanguage language) => language switch
    {
        AppLanguage.Persian => "fa",
        _ => "en",
    };

    /// <summary>The language's own name, used on the button that switches to it.</summary>
    public static string NativeName(this AppLanguage language) => language switch
    {
        AppLanguage.Persian => "فارسی",
        _ => "English",
    };

    /// <summary>Persian is written right-to-left; the whole desktop layout mirrors for it.</summary>
    public static bool IsRightToLeft(this AppLanguage language) => language == AppLanguage.Persian;

    /// <summary>Parses a stored tag, falling back to English for anything unrecognised.</summary>
    public static AppLanguage Parse(string? tag) =>
        string.Equals(tag?.Trim(), "fa", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Persian
            : AppLanguage.English;
}
