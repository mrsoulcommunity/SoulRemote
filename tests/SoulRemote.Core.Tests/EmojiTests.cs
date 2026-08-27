using SoulRemote.Localization;
using SoulRemote.Models;
using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

/// <summary>
/// Finding emoji in a string and swapping them for premium ones.
///
/// Nearly every case here is a way of cutting a character in half. An emoji is one
/// thing to a reader and between one and eleven UTF-16 units to a program, and a
/// substitution that gets the boundary wrong does not produce a wrong emoji — it
/// produces markup Telegram rejects, which loses the whole message.
/// </summary>
public sealed class EmojiTextTests
{
    private const string Id = "5368324170671202286";

    [Theory]
    [InlineData("📸", 2)]        // surrogate pair
    [InlineData("⚡", 1)]        // one BMP code point
    [InlineData("⌨️", 2)]        // code point + variation selector
    [InlineData("▫️", 2)]
    [InlineData("⬜", 1)]
    [InlineData("←", 1)]
    public void An_emoji_is_measured_whole(string emoji, int expected)
    {
        Assert.Equal(expected, EmojiText.SequenceLengthAt(emoji, 0));
    }

    [Fact]
    public void A_zwj_sequence_is_one_emoji()
    {
        // Four code points and a joiner, but one character on screen. Splitting it
        // would leave "a woman" and "a laptop" side by side.
        const string worker = "👩‍💻";
        Assert.Equal(worker.Length, EmojiText.SequenceLengthAt(worker, 0));
    }

    [Fact]
    public void A_skin_tone_travels_with_the_emoji_it_modifies()
    {
        const string wave = "👋🏽";
        Assert.Equal(wave.Length, EmojiText.SequenceLengthAt(wave, 0));
    }

    [Theory]
    [InlineData("Capture")]
    [InlineData("تصویربرداری")]
    [InlineData("")]
    [InlineData("·")]
    public void Ordinary_text_holds_no_emoji(string text)
    {
        Assert.Empty(EmojiText.Distinct(text));
    }

    [Fact]
    public void Distinct_finds_each_emoji_once_in_order()
    {
        Assert.Equal(new[] { "📸", "⚡", "⌨️" }, EmojiText.Distinct("📸 a ⚡ b 📸 c ⌨️"));
    }

    [Fact]
    public void A_mapped_emoji_is_wrapped_and_kept()
    {
        var html = EmojiText.ApplyPremium("📸 <b>Capture</b>", new Dictionary<string, string> { ["📸"] = Id });

        // Wrapped, not replaced: Telegram shows the character inside the tag wherever
        // it cannot draw the custom one, and ignores an entity that wraps nothing.
        Assert.Equal($"<tg-emoji emoji-id=\"{Id}\">📸</tg-emoji> <b>Capture</b>", html);
    }

    [Fact]
    public void An_unmapped_emoji_is_left_exactly_as_it_was()
    {
        const string text = "⚡ <b>Power</b>";
        var html = EmojiText.ApplyPremium(text, new Dictionary<string, string> { ["📸"] = Id });

        // The same instance, not merely an equal one: the client tells a decorated
        // message from an untouched one by reference.
        Assert.Same(text, html);
    }

    [Fact]
    public void Nothing_inside_pre_or_code_is_rewritten()
    {
        // A <pre> block holds shell output and clipboard text — someone else's data.
        var map = new Dictionary<string, string> { ["📸"] = Id };
        Assert.Same("<pre>echo 📸</pre>", EmojiText.ApplyPremium("<pre>echo 📸</pre>", map));
        Assert.Same("<code>📸</code>", EmojiText.ApplyPremium("<code>📸</code>", map));
    }

    [Fact]
    public void An_emoji_after_a_pre_block_is_still_converted()
    {
        var html = EmojiText.ApplyPremium("<pre>📸</pre> 📸",
            new Dictionary<string, string> { ["📸"] = Id });

        Assert.Equal($"<pre>📸</pre> <tg-emoji emoji-id=\"{Id}\">📸</tg-emoji>", html);
    }

    [Fact]
    public void Unterminated_markup_is_copied_rather_than_guessed_at()
    {
        const string broken = "<b 📸";
        Assert.Same(broken, EmojiText.ApplyPremium(broken, new Dictionary<string, string> { ["📸"] = Id }));
    }

    [Fact]
    public void An_invalid_identifier_is_never_sent()
    {
        // Telegram answers a malformed id with a 400 that costs the whole message, so
        // a mapping that could not work is skipped rather than attempted.
        const string text = "📸";
        Assert.Same(text, EmojiText.ApplyPremium(text, new Dictionary<string, string> { ["📸"] = "not-a-number" }));
    }

    [Fact]
    public void A_decorated_message_still_splits_without_breaking_a_tag()
    {
        // The splitter runs first and the tags go in afterwards, but the two have to
        // agree: a piece that ends mid-tag is a message Telegram refuses outright.
        var line = new string('x', 200) + " 📸\n";
        var body = string.Concat(Enumerable.Repeat(line, 40));
        var map = new Dictionary<string, string> { ["📸"] = Id };

        foreach (var piece in TextUtil.SplitForTelegram(body, 4000))
        {
            var decorated = EmojiText.ApplyPremium(piece, map);
            Assert.Equal(
                decorated.Split("<tg-emoji", StringSplitOptions.None).Length,
                decorated.Split("</tg-emoji>", StringSplitOptions.None).Length);
        }
    }

    [Theory]
    [InlineData("📸 Capture", "📸", "Capture")]
    [InlineData("✅ Yes, do it", "✅", "Yes, do it")]
    [InlineData("Monitor 1", "", "Monitor 1")]
    [InlineData("", "", "")]
    public void A_caption_splits_into_its_emoji_and_its_words(string caption, string emoji, string label)
    {
        var (gotEmoji, gotLabel) = EmojiText.SplitLeadingEmoji(caption);
        Assert.Equal(emoji, gotEmoji);
        Assert.Equal(label, gotLabel);
    }

    [Fact]
    public void A_caption_that_is_only_an_emoji_keeps_it()
    {
        // Moving it to the icon field would leave a button with no label at all on any
        // client that will not draw the icon.
        var (emoji, label) = EmojiText.SplitLeadingEmoji("📸");
        Assert.Equal(string.Empty, emoji);
        Assert.Equal("📸", label);
    }

    [Theory]
    [InlineData("5368324170671202286", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("12a4", false)]
    [InlineData("-5368324170671202286", false)]
    [InlineData("999999999999999999999", false)]      // past int64
    [InlineData("18446744073709551615", false)]       // fits uint64, not int64
    public void An_identifier_is_a_non_zero_int64_in_decimal(string? id, bool valid)
    {
        Assert.Equal(valid, EmojiText.IsValidCustomEmojiId(id));
    }

    [Fact]
    public void Folding_ignores_the_invisible_variation_selector()
    {
        Assert.Equal(EmojiText.Fold("⚙️"), EmojiText.Fold("⚙"));
        Assert.Equal("📸", EmojiText.Fold("📸"));
    }
}

/// <summary>
/// The list of emoji the settings screens offer. It is derived from the string
/// catalogue rather than written out, so what is checked here is that the
/// derivation actually finds what the bot sends.
/// </summary>
public sealed class EmojiCatalogTests
{
    [Fact]
    public void The_catalogue_holds_the_emoji_the_bot_uses()
    {
        // A spread across menus, screens and results. If the scan stopped working,
        // these are the first things a user would notice missing from the list.
        foreach (var emoji in new[] { "📸", "⚡", "🔒", "✅", "❌", "⚙️", "⌨️", "🎛", "⬜", "⚠️" })
            Assert.Contains(EmojiCatalog.All, e => e.Emoji == emoji);
    }

    [Fact]
    public void Desktop_only_emoji_are_left_out()
    {
        // The catalogue drives a setting about what the *bot* sends. An arrow that only
        // ever appears in a Cloudflare error in the window would be a row promising a
        // conversion that could never show up anywhere.
        Assert.DoesNotContain(EmojiCatalog.All, e => e.Emoji == "←");
    }

    [Fact]
    public void The_order_is_the_same_every_time()
    {
        // Payloads carry an index into this list, and a panel can sit in a chat for
        // hours. An order that shuffled would make an old button convert the wrong one.
        Assert.Equal(EmojiCatalog.All.Select(e => e.Emoji), EmojiCatalog.All.Select(e => e.Emoji));
        Assert.Equal(EmojiCatalog.All[0].Emoji, EmojiCatalog.All[0].Emoji);
    }

    [Fact]
    public void Every_emoji_has_a_name_in_both_languages()
    {
        foreach (var use in EmojiCatalog.All)
        {
            foreach (var language in Enum.GetValues<AppLanguage>())
            {
                var label = EmojiCatalog.LabelFor(use.Emoji, language);
                Assert.False(string.IsNullOrWhiteSpace(label), $"{use.Emoji} has no {language} name");
                Assert.DoesNotContain("<", label, StringComparison.Ordinal);
                Assert.DoesNotContain("{0}", label, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Nothing_is_listed_twice()
    {
        Assert.Equal(EmojiCatalog.All.Count, EmojiCatalog.All.Select(e => e.Emoji).Distinct().Count());
    }

    [Theory]
    // A menu caption beats the sentence that happens to start with the same emoji:
    // 📸 opens Capture, and "Screenshot failed" is not what it is called.
    [InlineData("📸", "Capture")]
    [InlineData("⚡", "Power")]
    [InlineData("🔒", "Lock")]
    // These appear only inside prose, so they are named by hand rather than derived.
    [InlineData("⚠️", "Warning")]
    [InlineData("⏱", "Uptime")]
    [InlineData("✅", "Switched on")]
    [InlineData("⬜", "Switched off")]
    public void An_emoji_is_named_after_what_it_marks(string emoji, string expected)
    {
        Assert.Equal(expected, EmojiCatalog.LabelFor(emoji, AppLanguage.English));
    }

    [Fact]
    public void A_name_never_carries_a_markup_escape()
    {
        // The catalogue is written for Telegram's HTML parser, so "&" is stored as
        // "&amp;". A chip reading "Startup &amp; notifications" is a leak, not a name.
        foreach (var use in EmojiCatalog.All)
        {
            foreach (var language in Enum.GetValues<AppLanguage>())
                Assert.DoesNotContain("&amp;", EmojiCatalog.LabelFor(use.Emoji, language), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_emoji_is_left_standing_in_for_its_own_name()
    {
        // The last-resort fallback is the character itself, which in a list of chips
        // reads as a blank one. Anything that lands there needs a name in Named.
        var nameless = EmojiCatalog.All
            .Where(e => Enum.GetValues<AppLanguage>()
                .Any(l => EmojiCatalog.LabelFor(e.Emoji, l) == e.Emoji))
            .Select(e => e.Emoji)
            .ToArray();

        Assert.True(nameless.Length == 0, "no name for: " + string.Join(" ", nameless));
    }

    [Fact]
    public void The_most_used_emoji_come_first()
    {
        var uses = EmojiCatalog.All.Select(e => e.Uses).ToArray();
        Assert.Equal(uses.OrderByDescending(u => u), uses);
    }
}

/// <summary>Turning an emoji pack into a mapping for the bot's own emoji.</summary>
public sealed class EmojiPackTests
{
    private static TgSticker Sticker(string emoji, string id) =>
        new() { Type = "custom_emoji", Emoji = emoji, CustomEmojiId = id, SetName = "Pack" };

    [Fact]
    public void A_pack_converts_the_emoji_it_has_versions_of()
    {
        var map = EmojiPack.Match(new[]
        {
            Sticker("📸", "1001"),
            Sticker("⚡", "1002"),
            Sticker("🦄", "1003"),   // the bot has no unicorn
        });

        Assert.Equal("1001", map["📸"]);
        Assert.Equal("1002", map["⚡"]);
        Assert.DoesNotContain("🦄", map.Keys);
    }

    [Fact]
    public void A_pack_that_writes_an_emoji_without_its_variation_selector_still_matches()
    {
        // ⚙ and ⚙️ are the same emoji to a reader. Matching them literally would lose
        // every pack whose author typed the bare form.
        var map = EmojiPack.Match(new[] { Sticker("⚙", "1004") });

        Assert.Equal("1004", map["⚙️"]);
    }

    [Fact]
    public void The_first_version_of_an_emoji_wins()
    {
        // Packs routinely carry several takes on the same character; picking a later
        // one would make the same import produce a different result each time.
        var map = EmojiPack.Match(new[] { Sticker("📸", "1001"), Sticker("📸", "9009") });

        Assert.Equal("1001", map["📸"]);
    }

    [Fact]
    public void A_sticker_with_no_usable_identifier_is_skipped()
    {
        var map = EmojiPack.Match(new[]
        {
            Sticker("📸", "0"),
            new TgSticker { Emoji = "⚡", CustomEmojiId = null },
            new TgSticker { Emoji = null, CustomEmojiId = "1002" },
        });

        Assert.Empty(map);
    }

    [Theory]
    [InlineData("https://t.me/addemoji/MyPack", "MyPack")]
    [InlineData("t.me/addemoji/MyPack", "MyPack")]
    [InlineData("  MyPack  ", "MyPack")]
    [InlineData("tg://addemoji?set=MyPack", "MyPack")]
    [InlineData("https://t.me/addemoji/MyPack?start=1", "MyPack")]
    [InlineData("https://t.me/addemoji/MyPack/", "MyPack")]
    public void A_pack_can_be_named_however_the_user_had_it_to_hand(string input, string expected)
    {
        Assert.Equal(expected, EmojiPack.NameFrom(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a name")]
    [InlineData("has-a-dash")]
    public void Anything_that_is_not_a_pack_name_is_refused(string? input)
    {
        Assert.Null(EmojiPack.NameFrom(input));
    }
}
