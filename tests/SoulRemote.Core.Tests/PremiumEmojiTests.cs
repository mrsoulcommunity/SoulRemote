using SoulRemote.Models;
using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

/// <summary>
/// Applying the premium look on the way out, and working out whether Telegram is
/// letting the bot do it at all.
///
/// That last part is the awkward one. A bot without the entitlement is not told so:
/// the message goes through with a 200 and its custom emoji quietly removed. The
/// only way to know is to look at what came back, which is why these tests are
/// mostly about the round trip rather than the substitution.
/// </summary>
public sealed class PremiumEmojiStylerTests
{
    private const string Id = "5368324170671202286";

    private static (PremiumEmojiStyler Styler, FakeSettings Settings) Build(
        bool enabled = true, Dictionary<string, string>? map = null)
    {
        var settings = new FakeSettings(new AppSettings
        {
            UsePremiumEmoji = enabled,
            PremiumEmoji = map ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["📸"] = Id },
        });
        return (new PremiumEmojiStyler(settings, new FakeLog()), settings);
    }

    private static TgMessage Echo(bool keptTheEntity) => new()
    {
        MessageId = 1,
        Entities = keptTheEntity
            ? new List<TgMessageEntity> { new() { Type = "custom_emoji", Offset = 0, Length = 2, CustomEmojiId = Id } }
            : new List<TgMessageEntity>(),
    };

    [Fact]
    public void Text_is_decorated_while_the_setting_is_on()
    {
        var (styler, _) = Build();
        Assert.Contains("<tg-emoji", styler.Decorate("📸 Capture"), StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_the_setting_off_leaves_the_mapping_alone()
    {
        var (styler, settings) = Build();
        var off = settings.Current.Clone();
        off.UsePremiumEmoji = false;
        settings.Save(off);

        Assert.DoesNotContain("<tg-emoji", styler.Decorate("📸 Capture"), StringComparison.Ordinal);
        // The map survives, so switching back on costs nothing.
        Assert.Single(settings.Current.PremiumEmoji);
    }

    [Fact]
    public void Buttons_are_left_alone_until_a_custom_emoji_has_been_seen_to_survive()
    {
        var (styler, _) = Build();
        var keyboard = new TgInlineKeyboardMarkup
        {
            InlineKeyboard = { new List<TgInlineKeyboardButton> { new("📸 Capture", "a:ss") } },
        };

        // Unknown entitlement: a button that gave its emoji to an icon field Telegram
        // then dropped would have lost the emoji altogether, so nothing moves yet.
        Assert.Same(keyboard, styler.DecorateMarkup(keyboard));

        styler.Observe($"<tg-emoji emoji-id=\"{Id}\">📸</tg-emoji>", Echo(keptTheEntity: true));

        var decorated = Assert.IsType<TgInlineKeyboardMarkup>(styler.DecorateMarkup(keyboard));
        var button = decorated.InlineKeyboard[0][0];
        Assert.Equal("Capture", button.Text);
        Assert.Equal(Id, button.IconCustomEmojiId);
        Assert.Equal("a:ss", button.CallbackData);

        // The caller's keyboard is untouched — the router reuses these objects.
        Assert.Equal("📸 Capture", keyboard.InlineKeyboard[0][0].Text);
    }

    [Fact]
    public void A_button_with_no_mapped_emoji_is_handed_back_as_it_was()
    {
        var (styler, _) = Build();
        styler.Observe($"<tg-emoji emoji-id=\"{Id}\">📸</tg-emoji>", Echo(keptTheEntity: true));

        var keyboard = new TgInlineKeyboardMarkup
        {
            InlineKeyboard = { new List<TgInlineKeyboardButton> { new("⚡ Power", "m:pwr") } },
        };
        Assert.Same(keyboard, styler.DecorateMarkup(keyboard));
    }

    [Fact]
    public void The_shortcut_bar_keeps_its_shape_when_its_emoji_move()
    {
        var (styler, _) = Build();
        styler.Observe($"<tg-emoji emoji-id=\"{Id}\">📸</tg-emoji>", Echo(keptTheEntity: true));

        var bar = new TgReplyKeyboardMarkup
        {
            Keyboard = { new List<TgKeyboardButton> { new("📸 Screenshot") } },
            Placeholder = "Tap a control",
            IsPersistent = true,
        };

        var decorated = Assert.IsType<TgReplyKeyboardMarkup>(styler.DecorateMarkup(bar));
        Assert.Equal("Screenshot", decorated.Keyboard[0][0].Text);
        Assert.Equal(Id, decorated.Keyboard[0][0].IconCustomEmojiId);
        Assert.Equal("Tap a control", decorated.Placeholder);
        Assert.True(decorated.IsPersistent);
    }

    [Fact]
    public void A_stripped_entity_is_read_as_a_refusal()
    {
        var (styler, _) = Build();
        var seen = new List<PremiumEmojiState>();
        styler.StateChanged += seen.Add;

        styler.Observe($"<tg-emoji emoji-id=\"{Id}\">📸</tg-emoji>", Echo(keptTheEntity: false));

        Assert.Equal(PremiumEmojiState.Refused, styler.State);
        Assert.Equal(new[] { PremiumEmojiState.Refused }, seen);
    }

    [Fact]
    public void A_message_that_carried_no_custom_emoji_teaches_nothing()
    {
        var (styler, _) = Build();
        styler.Observe("plain text", Echo(keptTheEntity: false));
        Assert.Equal(PremiumEmojiState.Unknown, styler.State);
    }

    [Fact]
    public void Changing_the_mapping_asks_the_question_again()
    {
        var (styler, settings) = Build();
        styler.Observe($"<tg-emoji emoji-id=\"{Id}\">📸</tg-emoji>", Echo(keptTheEntity: false));
        Assert.Equal(PremiumEmojiState.Refused, styler.State);

        var changed = settings.Current.Clone();
        changed.PremiumEmoji["⚡"] = "1234567890";
        settings.Save(changed);

        // Being told forever that something you have since changed is broken is worse
        // than testing it once more on the next message.
        Assert.Equal(PremiumEmojiState.Unknown, styler.State);
    }

    [Fact]
    public void An_outright_refusal_is_recorded_too()
    {
        var (styler, _) = Build();
        styler.ReportRejected("Bad Request: Invalid custom emoji identifier specified");
        Assert.Equal(PremiumEmojiState.Refused, styler.State);
    }

    [Fact]
    public void Nothing_more_is_decorated_once_telegram_has_refused()
    {
        // A refusal over a malformed identifier repeats on every message: without this
        // the bot would send each reply twice for the rest of the session, once to be
        // rejected and once plain.
        var (styler, _) = Build();
        const string text = "📸 Capture";
        Assert.NotSame(text, styler.Decorate(text));

        styler.ReportRejected("Bad Request: Invalid custom emoji identifier specified");

        Assert.Same(text, styler.Decorate(text));
        Assert.False(styler.IsActive);
    }

    [Fact]
    public void Fixing_the_mapping_gets_it_tried_again()
    {
        var (styler, settings) = Build();
        styler.ReportRejected("Bad Request: Invalid custom emoji identifier specified");

        var fixedUp = settings.Current.Clone();
        fixedUp.PremiumEmoji["📸"] = "9999999999999999";
        settings.Save(fixedUp);

        Assert.NotSame("📸 Capture", styler.Decorate("📸 Capture"));
    }

    [Fact]
    public void An_empty_mapping_decorates_nothing()
    {
        var (styler, _) = Build(map: new Dictionary<string, string>(StringComparer.Ordinal));
        const string text = "📸 Capture";
        Assert.Same(text, styler.Decorate(text));
        Assert.False(styler.IsActive);
    }
}

/// <summary>Adopting premium emoji, from a whole pack or one at a time.</summary>
public sealed class EmojiImporterTests
{
    private static TgSticker Sticker(string emoji, string id, string set = "MyPack") =>
        new() { Type = "custom_emoji", Emoji = emoji, CustomEmojiId = id, SetName = set };

    private static (EmojiImporter Importer, FakeSettings Settings, FakeTelegram Telegram) Build()
    {
        var settings = new FakeSettings(new AppSettings());
        var telegram = new FakeTelegram();
        telegram.StickerSets["MyPack"] = new TgStickerSet
        {
            Name = "MyPack",
            Title = "My Pack",
            StickerType = "custom_emoji",
            Stickers = { Sticker("📸", "1001"), Sticker("⚡", "1002"), Sticker("🦄", "1003") },
        };
        telegram.CustomEmoji["1001"] = Sticker("📸", "1001");
        telegram.CustomEmoji["1003"] = Sticker("🦄", "1003");
        return (new EmojiImporter(settings, telegram), settings, telegram);
    }

    [Fact]
    public async Task A_pack_link_converts_everything_the_pack_covers()
    {
        var (importer, settings, _) = Build();

        var message = await importer.ImportPackAsync("https://t.me/addemoji/MyPack", null);

        Assert.Equal("1001", settings.Current.PremiumEmoji["📸"]);
        Assert.Equal("1002", settings.Current.PremiumEmoji["⚡"]);
        Assert.Equal("MyPack", settings.Current.PremiumEmojiPack);
        Assert.True(settings.Current.UsePremiumEmoji);
        // The reply says how many, because a pack converts what it has and no more.
        Assert.Contains("2", message, StringComparison.Ordinal);
        Assert.Contains("My Pack", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_emoji_from_a_pack_is_enough_to_find_the_pack()
    {
        var (importer, settings, telegram) = Build();
        var entities = new List<TgMessageEntity>
        {
            new() { Type = "custom_emoji", Offset = 0, Length = 2, CustomEmojiId = "1001" },
        };

        await importer.ImportPackAsync(string.Empty, entities);

        Assert.Equal(2, settings.Current.PremiumEmoji.Count);
        Assert.Contains(telegram.CustomEmojiLookups, ids => ids.Contains("1001"));
    }

    [Fact]
    public async Task A_pack_replaces_the_map_rather_than_joining_it()
    {
        var (importer, settings, _) = Build();
        var seeded = settings.Current.Clone();
        seeded.PremiumEmoji["🔒"] = "9009";
        settings.Save(seeded);

        await importer.ImportPackAsync("MyPack", null);

        // "Use this pack" is the request; a map half one pack and half another would
        // be a look nobody chose.
        Assert.DoesNotContain("🔒", settings.Current.PremiumEmoji.Keys);
    }

    [Fact]
    public async Task A_sticker_pack_is_refused_with_its_name()
    {
        var (importer, _, telegram) = Build();
        telegram.StickerSets["Cats"] = new TgStickerSet { Name = "Cats", Title = "Cats", StickerType = "regular" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportPackAsync("Cats", null));
        Assert.Contains("Cats", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pack_with_nothing_in_common_says_so_instead_of_saving_nothing()
    {
        var (importer, settings, telegram) = Build();
        telegram.StickerSets["Unicorns"] = new TgStickerSet
        {
            Name = "Unicorns", Title = "Unicorns", StickerType = "custom_emoji",
            Stickers = { Sticker("🦄", "2001", "Unicorns") },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportPackAsync("Unicorns", null));
        Assert.Empty(settings.Current.PremiumEmoji);
    }

    [Fact]
    public async Task A_pasted_identifier_converts_the_emoji_it_stands_for()
    {
        var (importer, settings, _) = Build();

        await importer.AdoptAsync(null, new[] { "1001" }, null);

        Assert.Equal("1001", settings.Current.PremiumEmoji["📸"]);
    }

    [Fact]
    public async Task A_premium_emoji_the_bot_never_shows_is_reported_not_stored()
    {
        var (importer, settings, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.AdoptAsync(null, new[] { "1003" }, null));

        // Naming the emoji it stands for is the whole explanation.
        Assert.Contains("🦄", ex.Message, StringComparison.Ordinal);
        Assert.Empty(settings.Current.PremiumEmoji);
    }

    [Fact]
    public async Task Aiming_at_one_emoji_refuses_a_version_of_a_different_one()
    {
        var (importer, settings, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.AdoptAsync(null, new[] { "1001" }, "⚡"));

        // Telegram only shows a custom emoji in place of the one it is a version of,
        // so the refusal has to name both.
        Assert.Contains("⚡", ex.Message, StringComparison.Ordinal);
        Assert.Contains("📸", ex.Message, StringComparison.Ordinal);
        Assert.Empty(settings.Current.PremiumEmoji);
    }

    [Fact]
    public async Task A_message_with_no_premium_emoji_in_it_says_so()
    {
        var (importer, _, _) = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.AdoptAsync(null, null, null));
    }

    [Fact]
    public async Task Clearing_one_leaves_the_rest()
    {
        var (importer, settings, _) = Build();
        await importer.ImportPackAsync("MyPack", null);

        importer.ClearOne("📸");

        Assert.DoesNotContain("📸", settings.Current.PremiumEmoji.Keys);
        Assert.Contains("⚡", settings.Current.PremiumEmoji.Keys);
        // The map is no longer that pack, so the label stops claiming it is.
        Assert.Equal(string.Empty, settings.Current.PremiumEmojiPack);
    }

    [Fact]
    public async Task Clearing_them_all_empties_the_map_and_the_pack_name()
    {
        var (importer, settings, _) = Build();
        await importer.ImportPackAsync("MyPack", null);

        importer.ClearAll();

        Assert.Empty(settings.Current.PremiumEmoji);
        Assert.Equal(string.Empty, settings.Current.PremiumEmojiPack);
    }

    [Fact]
    public async Task An_import_that_did_not_reach_disk_does_not_claim_it_did()
    {
        var (importer, settings, _) = Build();
        settings.FailSaves = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportPackAsync("MyPack", null));
    }
}

/// <summary>Where the mapping is kept, and what survives a round trip through it.</summary>
public sealed class PremiumEmojiSettingsTests
{
    [Fact]
    public void Cloning_copies_the_map_rather_than_sharing_it()
    {
        // The poll thread reads the live settings while the UI edits a clone; a shared
        // dictionary would let one see the other's half-finished work.
        var settings = new AppSettings();
        settings.PremiumEmoji["📸"] = "1001";

        var clone = settings.Clone();
        clone.PremiumEmoji["⚡"] = "1002";

        Assert.Single(settings.PremiumEmoji);
        Assert.Equal(2, clone.PremiumEmoji.Count);
    }

    [Fact]
    public void An_identifier_telegram_would_refuse_is_dropped_on_normalize()
    {
        var settings = new AppSettings();
        settings.PremiumEmoji["📸"] = "1001";
        settings.PremiumEmoji["⚡"] = "not-a-number";
        settings.PremiumEmoji["🔒"] = "0";

        settings.Normalize();

        Assert.Equal(new[] { "📸" }, settings.PremiumEmoji.Keys);
    }

    [Fact]
    public void An_emoji_this_build_no_longer_uses_is_kept()
    {
        // Only the identifier is judged. A release that reworded one string is no
        // reason to quietly delete part of a pack the user imported.
        var settings = new AppSettings();
        settings.PremiumEmoji["🦄"] = "1001";

        settings.Normalize();

        Assert.Contains("🦄", settings.PremiumEmoji.Keys);
    }

    [Fact]
    public void A_mapping_for_an_emoji_the_bot_dropped_is_not_counted_as_converted()
    {
        // Those entries are kept rather than deleted, but they convert nothing, and
        // counting them would let the panel read "68 of 67 converted".
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["📸"] = "1001",
            ["🦄"] = "1002",
        };

        Assert.Equal(1, EmojiCatalog.ConvertedCount(map));
        Assert.Equal(0, EmojiCatalog.ConvertedCount(null));
    }

    [Fact]
    public void The_mapping_survives_being_written_and_read_back()
    {
        var path = Path.Combine(Path.GetTempPath(), "soulremote-emoji-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new SettingsService(new FakeLog(), settingsPath: path);
            var settings = new AppSettings { PremiumEmojiPack = "MyPack" };
            settings.PremiumEmoji["📸"] = "5368324170671202286";
            settings.PremiumEmoji["⌨️"] = "1002";

            Assert.True(service.Save(settings));

            var reloaded = new SettingsService(new FakeLog(), settingsPath: path).Load();
            Assert.Equal("5368324170671202286", reloaded.PremiumEmoji["📸"]);
            // The variation selector has to come back too, or the key stops matching
            // the character the bot actually sends.
            Assert.Equal("1002", reloaded.PremiumEmoji["⌨️"]);
            Assert.Equal("MyPack", reloaded.PremiumEmojiPack);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
