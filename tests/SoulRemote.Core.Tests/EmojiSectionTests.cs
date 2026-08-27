using SoulRemote.Localization;
using SoulRemote.Models;
using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

/// <summary>
/// The bot's premium-emoji section, driven the way a phone drives it: taps and
/// messages in, panels and saved settings out.
/// </summary>
public sealed class EmojiSectionTests
{
    private const long Owner = 1001;

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeSettings Settings { get; }
        public FakeTelegram Telegram { get; } = new();
        public FakeSystem System { get; } = new();
        public CommandRouter Router { get; }

        public PremiumEmojiStyler Emoji { get; }

        public Harness(Action<AppSettings>? configure = null)
        {
            var settings = new AppSettings { AuthorizedChatIds = { Owner } };
            configure?.Invoke(settings);
            Settings = new FakeSettings(settings);
            Emoji = new PremiumEmojiStyler(Settings, new FakeLog());

            Telegram.StickerSets["MyPack"] = new TgStickerSet
            {
                Name = "MyPack",
                Title = "My Pack",
                StickerType = "custom_emoji",
                Stickers =
                {
                    Sticker("📸", "1001"),
                    Sticker("⚡", "1002"),
                    Sticker("🔒", "1003"),
                },
            };
            Telegram.CustomEmoji["1001"] = Sticker("📸", "1001");
            Telegram.CustomEmoji["2001"] = Sticker("🦄", "2001");

            Router = new CommandRouter(Settings, Telegram, System, new FakeScreenshots(),
                new FakeInfo(), new FakeLog(), Clock, pc: new FakePcSettings(), startup: new FakeStartup(),
                emoji: Emoji);
        }

        /// <summary>
        /// Puts the styler where it lands after a custom emoji has survived the round
        /// trip to Telegram, which is the only state in which button labels give their
        /// emoji up to the icon field.
        /// </summary>
        public void PremiumIsWorking() => Emoji.Observe(
            "<tg-emoji emoji-id=\"1001\">📸</tg-emoji>",
            new TgMessage
            {
                Entities = new List<TgMessageEntity>
                {
                    new() { Type = "custom_emoji", Offset = 0, Length = 2, CustomEmojiId = "1001" },
                },
            });

        private static TgSticker Sticker(string emoji, string id) =>
            new() { Type = "custom_emoji", Emoji = emoji, CustomEmojiId = id, SetName = "MyPack" };

        public Task Tap(long chatId, string data) =>
            Router.HandleUpdateAsync(new TgUpdate
            {
                UpdateId = 2,
                CallbackQuery = new TgCallbackQuery
                {
                    Id = "cb1",
                    Data = data,
                    From = new TgUser { Id = chatId },
                    Message = new TgMessage { MessageId = 42, Chat = new TgChat { Id = chatId, Type = "private" } },
                },
            }, CancellationToken.None);

        /// <param name="entities">
        /// What a premium emoji actually looks like arriving: the plain emoji in the
        /// text, and an entity beside it naming the custom one.
        /// </param>
        public Task Send(long chatId, string text, params string[] customEmojiIds) =>
            Router.HandleUpdateAsync(new TgUpdate
            {
                UpdateId = 1,
                Message = new TgMessage
                {
                    MessageId = 5,
                    Text = text,
                    Chat = new TgChat { Id = chatId, Type = "private" },
                    From = new TgUser { Id = chatId, FirstName = "Sara" },
                    Entities = customEmojiIds.Length == 0 ? null : customEmojiIds
                        .Select(id => new TgMessageEntity
                        {
                            Type = "custom_emoji", Offset = 0, Length = 2, CustomEmojiId = id,
                        })
                        .ToList(),
                },
            }, CancellationToken.None);

        public IReadOnlyList<string> Panel =>
            Telegram.Edits.Count > 0 ? Telegram.Edits[^1].Payloads : Telegram.Messages[^1].Payloads;

        public string PanelText =>
            Telegram.Edits.Count > 0 ? Telegram.Edits[^1].Text : Telegram.Messages[^1].Text;

        /// <summary>
        /// Every message body the bot has sent. A refused answer produces two — what
        /// went wrong, and then the panel again so the chat is left somewhere useful —
        /// so a test about the explanation has to look at both.
        /// </summary>
        public string AllText => string.Join(" | ", Telegram.Messages.Select(m => m.Text));
    }

    public EmojiSectionTests() => Strings.Use(AppLanguage.English);

    [Fact]
    public async Task The_settings_screen_offers_the_emoji_section()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:set");
        Assert.Contains("m:semj", h.Panel);
    }

    [Fact]
    public async Task Slash_emoji_opens_it_directly()
    {
        var h = new Harness();
        await h.Send(Owner, "/emoji");
        Assert.Contains("i:epk", h.Panel);
    }

    [Fact]
    public async Task The_panel_leads_with_how_many_were_converted()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:semj");

        // "0 of 64" is the honest opening line, and the number of emoji the bot has is
        // whatever the catalogue found.
        Assert.Contains(EmojiCatalog.Count.ToString(), h.PanelText, StringComparison.Ordinal);
        Assert.Contains("i:epk", h.Panel);
        Assert.Contains("m:seml.0", h.Panel);
        // Nothing to undo yet, so nothing offering to.
        Assert.DoesNotContain("c:ecl", h.Panel);
    }

    [Fact]
    public async Task Importing_a_pack_converts_everything_it_covers()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:epk");
        await h.Send(Owner, "https://t.me/addemoji/MyPack");

        Assert.Equal(3, h.Settings.Current.PremiumEmoji.Count);
        Assert.Equal("1001", h.Settings.Current.PremiumEmoji["📸"]);
        Assert.Equal("MyPack", h.Settings.Current.PremiumEmojiPack);
        Assert.True(h.Settings.Current.UsePremiumEmoji);
    }

    [Fact]
    public async Task Sending_one_emoji_from_a_pack_imports_the_whole_pack()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:epk");
        // A premium camera arrives as "📸" plus an entity — the text alone says nothing.
        await h.Send(Owner, "📸", "1001");

        Assert.Equal(3, h.Settings.Current.PremiumEmoji.Count);
    }

    [Fact]
    public async Task Adding_one_premium_emoji_converts_the_emoji_it_stands_for()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:eadd");
        await h.Send(Owner, "📸", "1001");

        Assert.Equal(new[] { "📸" }, h.Settings.Current.PremiumEmoji.Keys);
    }

    [Fact]
    public async Task A_premium_emoji_the_bot_never_shows_is_explained_rather_than_stored()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:eadd");
        await h.Send(Owner, "🦄", "2001");

        Assert.Empty(h.Settings.Current.PremiumEmoji);
        Assert.Contains("🦄", h.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_pages_and_every_entry_leads_somewhere()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:seml.0");

        Assert.Contains("m:semj", h.Panel);
        Assert.Contains("m:seml.1", h.Panel);
        // Nothing is converted yet, so every emoji offers to be.
        Assert.Contains(h.Panel, p => p.StartsWith("i:eone.", StringComparison.Ordinal));

        // Every payload on the page is a real action, and no button carries a dead one.
        Assert.All(h.Panel, p => Assert.True(p.Length <= 64, $"payload '{p}' is over Telegram's limit"));
    }

    [Fact]
    public async Task A_page_past_the_end_lands_on_the_last_one_instead_of_an_empty_panel()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:seml.999");

        Assert.Contains("m:semj", h.Panel);
        Assert.DoesNotContain("m:seml.1000", h.Panel);
    }

    [Fact]
    public async Task A_converted_emoji_offers_to_be_undone_and_is()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:epk");
        await h.Send(Owner, "MyPack");

        var index = EmojiCatalog.All.ToList().FindIndex(e => e.Emoji == "📸");
        await h.Tap(Owner, $"s:erm.{index}");

        Assert.DoesNotContain("📸", h.Settings.Current.PremiumEmoji.Keys);
        Assert.Contains("⚡", h.Settings.Current.PremiumEmoji.Keys);
    }

    [Fact]
    public async Task Tapping_one_emoji_only_accepts_a_version_of_that_emoji()
    {
        var h = new Harness();
        var index = EmojiCatalog.All.ToList().FindIndex(e => e.Emoji == "⚡");

        await h.Tap(Owner, $"i:eone.{index}");
        await h.Send(Owner, "📸", "1001");   // a camera, offered for the lightning bolt

        Assert.Empty(h.Settings.Current.PremiumEmoji);
        Assert.Contains("⚡", h.AllText, StringComparison.Ordinal);
        Assert.Contains("📸", h.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_them_all_is_confirmed_first()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:epk");
        await h.Send(Owner, "MyPack");

        await h.Tap(Owner, "c:ecl");
        Assert.Contains("y:ecl", h.Panel);
        // Still there — a confirmation screen changes nothing on its own.
        Assert.NotEmpty(h.Settings.Current.PremiumEmoji);

        await h.Tap(Owner, "y:ecl");
        Assert.Empty(h.Settings.Current.PremiumEmoji);
        Assert.Equal(string.Empty, h.Settings.Current.PremiumEmojiPack);
    }

    [Fact]
    public async Task The_look_can_be_switched_off_without_losing_the_mapping()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:epk");
        await h.Send(Owner, "MyPack");

        await h.Tap(Owner, "s:t.pemj.0");

        Assert.False(h.Settings.Current.UsePremiumEmoji);
        Assert.Equal(3, h.Settings.Current.PremiumEmoji.Count);
    }

    [Fact]
    public async Task A_read_only_panel_shows_the_state_and_offers_only_the_way_back()
    {
        var h = new Harness(s => s.AllowRemoteSettings = false);
        await h.Tap(Owner, "m:semj");

        Assert.Contains(Strings.Get("bot.set.readonly"), h.PanelText, StringComparison.Ordinal);
        Assert.Equal(new[] { "m:set" }, h.Panel);
    }

    [Fact]
    public async Task A_stale_button_pointing_past_the_catalogue_is_refused_not_obeyed()
    {
        var h = new Harness();
        await h.Tap(Owner, "s:erm.99999");

        Assert.Equal(0, h.Settings.SaveCount);
    }

    [Fact]
    public async Task The_shortcut_bar_still_works_once_its_emoji_have_moved_to_the_icons()
    {
        // With premium icons the bar's labels lose their emoji, so a tap comes back as
        // the bare caption. That form has to be recognised, because a bar pinned in a
        // chat outlives the setting that changed it.
        var h = new Harness();
        await h.Tap(Owner, "i:epk");
        await h.Send(Owner, "MyPack");
        h.PremiumIsWorking();

        await h.Send(Owner, "Screenshot");

        Assert.Single(h.Telegram.Files);
        Assert.True(h.Telegram.Files[0].IsPhoto);
    }

    [Fact]
    public async Task A_bare_caption_is_not_a_command_while_the_bar_still_wears_its_emoji()
    {
        // Nothing has been converted, so the bar reads "📸 Screenshot" and the word on
        // its own is just a word. Treating it as a command would hand every user four
        // one-word shortcuts they never asked for.
        var h = new Harness();
        await h.Send(Owner, "Screenshot");

        Assert.Empty(h.Telegram.Files);
        // Anything unrecognised opens the panel, which is what should have happened.
        Assert.Contains("m:cap", h.Panel);
    }

    [Fact]
    public async Task A_pending_prompt_is_not_stolen_by_a_word_that_matches_a_bar_button()
    {
        // This is the one that matters. The bar is checked before the pending prompt,
        // so a bare "Lock" recognised as a shortcut would lock the machine instead of
        // being typed into the focused window — and 🔒 is the emoji the bot uses most,
        // so very nearly any pack turns it on.
        var h = new Harness(s => s.AllowInputInjection = true);
        await h.Tap(Owner, "i:epk");
        await h.Send(Owner, "MyPack");
        h.PremiumIsWorking();

        await h.Tap(Owner, "i:type");
        await h.Send(Owner, "Lock");

        Assert.Equal("Lock", h.System.TypedText);
        Assert.DoesNotContain("lock", h.System.Calls);
    }

    [Fact]
    public async Task A_real_bar_tap_still_works_while_nothing_is_pending()
    {
        // The other half of the same rule: with no question outstanding, the bare
        // caption is a tap on a bar wearing premium icons, and has to act like one.
        var h = new Harness();
        await h.Tap(Owner, "i:epk");
        await h.Send(Owner, "MyPack");
        h.PremiumIsWorking();

        await h.Send(Owner, "Lock");

        Assert.Contains("lock", h.System.Calls);
    }

    [Fact]
    public async Task A_stale_button_for_one_emoji_converts_nothing()
    {
        // The index no longer names an emoji. Falling back to "convert whatever was
        // sent" would convert something the user never pointed at and call it a success.
        var h = new Harness();
        await h.Tap(Owner, "i:eone.99999");
        await h.Send(Owner, "📸", "1001");

        Assert.Empty(h.Settings.Current.PremiumEmoji);
    }

    [Fact]
    public async Task An_undo_comes_back_to_the_page_it_was_made_on()
    {
        // Being thrown back to page one after every edit turns converting a list of
        // sixty-eight into an exercise in paging.
        var index = EmojiCatalog.All.Count - 1;
        var page = index / BotMenu.EmojiPageSize;
        Assert.True(page > 0, "the catalogue is too short for this test to mean anything");

        var emoji = EmojiCatalog.All[index].Emoji;
        var h = new Harness(s => s.PremiumEmoji[emoji] = "1001");

        await h.Tap(Owner, $"s:erm.{index}");

        Assert.DoesNotContain(emoji, h.Settings.Current.PremiumEmoji.Keys);
        Assert.Contains(Strings.Format("bot.set.emoji.list.page", page + 1,
                (EmojiCatalog.Count + BotMenu.EmojiPageSize - 1) / BotMenu.EmojiPageSize),
            h.PanelText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_list_escapes_the_names_it_prints()
    {
        // With the buttons gone the captions are printed into the message body, which
        // Telegram parses as HTML. A name like "Startup & notifications" has to arrive
        // with its ampersand escaped, or the whole reply is rejected as bad markup.
        var h = new Harness(s => s.AllowRemoteSettings = false);

        for (var page = 0; page * BotMenu.EmojiPageSize < EmojiCatalog.Count; page++)
        {
            await h.Tap(Owner, $"m:seml.{page}");
            AssertEveryAmpersandIsAnEntity(h.PanelText);
        }
    }

    /// <summary>A bare "&amp;" in text sent as HTML is malformed markup, not a character.</summary>
    private static void AssertEveryAmpersandIsAnEntity(string html)
    {
        for (var i = html.IndexOf('&'); i >= 0; i = html.IndexOf('&', i + 1))
        {
            var tail = html[i..];
            Assert.True(
                tail.StartsWith("&amp;", StringComparison.Ordinal)
                || tail.StartsWith("&lt;", StringComparison.Ordinal)
                || tail.StartsWith("&gt;", StringComparison.Ordinal),
                $"unescaped '&' at {i} in: {html}");
        }
    }
}
