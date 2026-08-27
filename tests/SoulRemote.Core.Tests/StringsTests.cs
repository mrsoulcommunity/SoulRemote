using System.Text.RegularExpressions;
using SoulRemote.Localization;
using Xunit;

namespace SoulRemote.Tests;

/// <summary>
/// The string catalogue is the one place where a mistake is invisible until a user
/// sees it, so it is checked mechanically rather than by eye: no half-translated
/// row, no placeholder that exists in one language and not the other, and no key
/// referenced from code that the table has never heard of.
/// </summary>
public sealed class StringsTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    [Fact]
    public void Catalogue_is_not_empty()
    {
        Assert.True(Strings.Keys.Count > 100, $"only {Strings.Keys.Count} strings — the catalogue looks truncated");
    }

    [Fact]
    public void Every_row_has_both_languages()
    {
        var blank = Strings.Keys
            .Where(k => string.IsNullOrWhiteSpace(Strings.Get(AppLanguage.English, k))
                     || string.IsNullOrWhiteSpace(Strings.Get(AppLanguage.Persian, k)))
            .ToArray();

        Assert.True(blank.Length == 0, "blank translation for: " + string.Join(", ", blank));
    }

    [Fact]
    public void Persian_is_actually_translated()
    {
        // A row whose Persian half is byte-identical to the English half is almost always
        // a forgotten translation. The genuine exceptions are technical literals.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // Pure layout — an emoji and a slot, with nothing to translate.
            "bot.ok", "bot.err", "bot.menu.language", "bot.files.sent", "bot.files.listing",
            "bot.files.folder", "bot.files.file",
            // Literals a user types verbatim; translating them would break the example.
            "bot.placeholder.open", "bot.placeholder.shell", "bot.placeholder.path",
            "bot.placeholder.folder", "bot.placeholder.emojipack",
            // The language picker names each language in that language, in both
            // directions — someone who cannot read the current one has to find theirs.
            "ui.settings.english", "ui.settings.persian",
        };

        var untranslated = Strings.Keys
            .Where(k => !allowed.Contains(k))
            .Where(k => string.Equals(Strings.Get(AppLanguage.English, k),
                                      Strings.Get(AppLanguage.Persian, k), StringComparison.Ordinal))
            .ToArray();

        Assert.True(untranslated.Length == 0, "Persian still reads as English for: " + string.Join(", ", untranslated));
    }

    [Fact]
    public void Both_languages_use_the_same_placeholders()
    {
        var mismatched = new List<string>();
        foreach (var key in Strings.Keys)
        {
            var en = Indices(Strings.Get(AppLanguage.English, key));
            var fa = Indices(Strings.Get(AppLanguage.Persian, key));
            if (!en.SetEquals(fa))
                mismatched.Add($"{key} (en:{Join(en)} fa:{Join(fa)})");
        }

        Assert.True(mismatched.Count == 0, "placeholder mismatch: " + string.Join("; ", mismatched));

        static string Join(HashSet<int> set) => "[" + string.Join(",", set.OrderBy(i => i)) + "]";
    }

    [Fact]
    public void Placeholders_start_at_zero_and_are_contiguous()
    {
        foreach (var key in Strings.Keys)
        {
            var indices = Indices(Strings.Get(AppLanguage.English, key));
            if (indices.Count == 0)
                continue;
            var expected = Enumerable.Range(0, indices.Count).ToHashSet();
            Assert.True(indices.SetEquals(expected),
                $"{key} uses placeholders {string.Join(",", indices.OrderBy(i => i))} — they must run 0..n-1");
        }
    }

    [Fact]
    public void Html_markup_is_balanced_in_both_languages()
    {
        // Bot replies are sent with parse_mode=HTML. An unbalanced tag makes Telegram
        // reject the whole message, so a stray "<b>" is a broken reply, not a typo.
        foreach (var key in Strings.Keys)
        {
            AssertBalanced(key, "en", Strings.Get(AppLanguage.English, key));
            AssertBalanced(key, "fa", Strings.Get(AppLanguage.Persian, key));
        }

        static void AssertBalanced(string key, string lang, string text)
        {
            foreach (var tag in new[] { "b", "i", "code", "pre" })
            {
                var open = Regex.Matches(text, $"<{tag}>").Count;
                var close = Regex.Matches(text, $"</{tag}>").Count;
                Assert.True(open == close, $"{key} [{lang}]: {open} <{tag}> vs {close} </{tag}>");
            }
        }
    }

    [Fact]
    public void Unknown_key_returns_the_key_rather_than_throwing()
    {
        Assert.Equal("no.such.key", Strings.Get("no.such.key"));
        Assert.False(Strings.Has("no.such.key"));
    }

    [Fact]
    public void Format_survives_a_template_that_does_not_match_its_arguments()
    {
        // Get() on an unknown key returns the key, which has no placeholders.
        var result = Strings.Format("no.such.key", "value");
        Assert.Equal("no.such.key", result);
    }

    [Fact]
    public void Use_switches_language_and_raises_the_event_once()
    {
        var original = Strings.Current;
        var fired = 0;
        void Handler() => fired++;
        Strings.LanguageChanged += Handler;
        try
        {
            Strings.Use(AppLanguage.Persian);
            Strings.Use(AppLanguage.Persian); // no-op: already there
            Assert.Equal(AppLanguage.Persian, Strings.Current);
            Assert.True(Strings.IsRightToLeft);
            Assert.Equal(1, fired);

            Strings.Use(AppLanguage.English);
            Assert.False(Strings.IsRightToLeft);
            Assert.Equal(2, fired);
        }
        finally
        {
            Strings.LanguageChanged -= Handler;
            Strings.Use(original);
        }
    }

    [Theory]
    [InlineData("fa", AppLanguage.Persian)]
    [InlineData("FA", AppLanguage.Persian)]
    [InlineData("en", AppLanguage.English)]
    [InlineData("", AppLanguage.English)]
    [InlineData(null, AppLanguage.English)]
    [InlineData("klingon", AppLanguage.English)]
    public void Language_tags_round_trip(string? tag, AppLanguage expected)
    {
        Assert.Equal(expected, AppLanguageExtensions.Parse(tag));
    }

    [Fact]
    public void Tag_of_parsed_tag_is_stable()
    {
        Assert.Equal("fa", AppLanguage.Persian.Tag());
        Assert.Equal("en", AppLanguage.English.Tag());
        Assert.Equal(AppLanguage.Persian, AppLanguageExtensions.Parse(AppLanguage.Persian.Tag()));
    }

    private static HashSet<int> Indices(string text) =>
        Placeholder.Matches(text).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
}
