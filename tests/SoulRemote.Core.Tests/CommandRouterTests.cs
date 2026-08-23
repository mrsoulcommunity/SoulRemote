using SoulRemote.Localization;
using SoulRemote.Models;
using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

/// <summary>
/// The router is where a tap in Telegram becomes something happening to a real PC,
/// so these are behaviour tests: what a chat is allowed to do, what it is told, and
/// what the machine is asked to do as a result.
/// </summary>
public sealed class CommandRouterTests
{
    private const long Owner = 1001;
    private const long Stranger = 2002;

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeSettings Settings { get; }
        public FakeTelegram Telegram { get; } = new();
        public FakeSystem System { get; } = new();
        public FakeScreenshots Screenshots { get; } = new();
        public FakeInfo Info { get; } = new();
        public FakeLog Log { get; } = new();
        public CommandRouter Router { get; }

        public Harness(Action<AppSettings>? configure = null)
        {
            var settings = new AppSettings { AuthorizedChatIds = { Owner } };
            configure?.Invoke(settings);
            Settings = new FakeSettings(settings);
            Router = new CommandRouter(Settings, Telegram, System, Screenshots, Info, Log, Clock);
        }

        public Task Text(long chatId, string text, string chatType = "private") =>
            Router.HandleUpdateAsync(new TgUpdate
            {
                UpdateId = 1,
                Message = new TgMessage
                {
                    MessageId = 5,
                    Text = text,
                    Chat = new TgChat { Id = chatId, Type = chatType },
                    From = new TgUser { Id = chatId, FirstName = "Sara", Username = "sara_k" },
                },
            }, CancellationToken.None);

        public Task Tap(long chatId, string data, long panelId = 42) =>
            Router.HandleUpdateAsync(new TgUpdate
            {
                UpdateId = 2,
                CallbackQuery = new TgCallbackQuery
                {
                    Id = "cb1",
                    Data = data,
                    From = new TgUser { Id = chatId },
                    Message = new TgMessage { MessageId = panelId, Chat = new TgChat { Id = chatId, Type = "private" } },
                },
            }, CancellationToken.None);
    }

    public CommandRouterTests() => Strings.Use(AppLanguage.English);

    // ---------- authorization ----------

    [Fact]
    public async Task An_unpaired_chat_is_told_how_to_pair_and_nothing_runs()
    {
        var h = new Harness();
        await h.Text(Stranger, "/lock");

        Assert.Empty(h.System.Calls);
        Assert.Contains(h.Telegram.Messages, m => m.Text.Contains("/pair", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tap_from_an_unpaired_chat_is_refused_out_loud()
    {
        var h = new Harness();
        await h.Tap(Stranger, "a:lock");

        Assert.Empty(h.System.Calls);
        // The refusal must reach the button, or it spins forever.
        Assert.Single(h.Telegram.Answers);
        Assert.True(h.Telegram.Answers[0].Alert);
    }

    [Fact]
    public async Task A_stranger_gets_a_few_replies_and_then_silence()
    {
        var h = new Harness();
        for (var i = 0; i < 10; i++)
            await h.Text(Stranger, "hello");

        // Every reply is an outbound relay call; a leaked bot URL must not become a pump.
        Assert.InRange(h.Telegram.Messages.Count, 1, 5);
    }

    // ---------- pairing ----------

    [Fact]
    public async Task The_right_code_pairs_the_chat_and_remembers_who_it_was()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Clear());
        h.Router.PairingCode = "123456";

        await h.Text(Stranger, "/pair 123456");

        Assert.Contains(Stranger, h.Settings.Current.AuthorizedChatIds);
        Assert.Equal("Sara (@sara_k)", h.Settings.Current.NameFor(Stranger));
    }

    [Fact]
    public async Task The_code_works_only_once()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Clear());
        h.Router.PairingCode = "123456";

        await h.Text(Stranger, "/pair 123456");
        await h.Text(3003, "/pair 123456");

        Assert.DoesNotContain(3003L, h.Settings.Current.AuthorizedChatIds);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Clear());
        h.Router.PairingCode = "123456";
        h.Clock.Advance(CommandRouter.PairingCodeLifetime + TimeSpan.FromMinutes(1));

        await h.Text(Stranger, "/pair 123456");

        Assert.Empty(h.Settings.Current.AuthorizedChatIds);
    }

    [Fact]
    public async Task One_chat_guessing_wrong_cannot_lock_another_chat_out()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Clear());
        h.Router.PairingCode = "123456";

        // A stranger burns through the whole attempt budget. The clock moves between
        // tries so the stranger throttle does not absorb them — without that, the
        // attempts never reach the counter and this test proves nothing.
        for (var i = 0; i < 8; i++)
        {
            await h.Text(4004, "/pair 000000");
            h.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Contains(h.Log.Messages, m => m.Contains("5/5", StringComparison.Ordinal));

        // ...and the owner, on a different chat, can still pair.
        await h.Text(Stranger, "/pair 123456");

        Assert.Contains(Stranger, h.Settings.Current.AuthorizedChatIds);
    }

    [Fact]
    public async Task Pairing_is_refused_in_a_group_chat()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Clear());
        h.Router.PairingCode = "123456";

        await h.Text(Stranger, "/pair 123456", chatType: "supergroup");

        // Pairing a group would hand control of the PC to everyone in it, including
        // whoever is added to it tomorrow.
        Assert.Empty(h.Settings.Current.AuthorizedChatIds);
    }

    [Fact]
    public async Task A_pairing_that_cannot_be_saved_is_not_reported_as_success()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Clear());
        h.Router.PairingCode = "123456";
        h.Settings.FailSaves = true;

        await h.Text(Stranger, "/pair 123456");

        Assert.Empty(h.Settings.Current.AuthorizedChatIds);
        // And the code must survive, or the user is left with neither access nor a code.
        Assert.Equal("123456", h.Router.PairingCode);
    }

    // ---------- routing ----------

    [Fact]
    public async Task A_typed_command_reaches_the_machine()
    {
        var h = new Harness();
        await h.Text(Owner, "/lock");

        Assert.Contains("lock", h.System.Calls);
    }

    [Fact]
    public async Task A_command_addressed_to_the_bot_by_name_still_routes()
    {
        var h = new Harness();
        await h.Text(Owner, "/lock@soul_remote_bot");

        Assert.Contains("lock", h.System.Calls);
    }

    [Fact]
    public async Task A_shortcut_bar_tap_is_recognised_before_a_pending_prompt_can_eat_it()
    {
        var h = new Harness(s => s.AllowInputInjection = true);
        await h.Tap(Owner, "i:type");          // "Type text…" — a prompt is now pending
        h.Telegram.Messages.Clear();

        await h.Text(Owner, BotMenu.BarLock);  // the user taps Lock on the shortcut bar

        // The old order typed the word "Lock" into the focused window instead.
        Assert.Null(h.System.TypedText);
        Assert.Contains("lock", h.System.Calls);
    }

    [Fact]
    public async Task A_shortcut_bar_tap_in_the_other_language_still_works()
    {
        var h = new Harness();
        // The pinned bar in Telegram keeps its old captions until it is replaced.
        var persianLock = Strings.Get(AppLanguage.Persian, "bot.bar.lock");

        await h.Text(Owner, persianLock);

        Assert.Contains("lock", h.System.Calls);
    }

    [Fact]
    public async Task A_prompt_collects_the_next_message_as_its_value()
    {
        var h = new Harness(s => s.AllowInputInjection = true);
        await h.Tap(Owner, "i:type");
        await h.Text(Owner, "hello world");

        Assert.Equal("hello world", h.System.TypedText);
    }

    [Fact]
    public async Task A_prompt_left_too_long_stops_claiming_messages()
    {
        var h = new Harness(s => s.AllowInputInjection = true);
        await h.Tap(Owner, "i:type");
        h.Clock.Advance(ChatPrompts.Lifetime + TimeSpan.FromMinutes(1));

        await h.Text(Owner, "hello world");

        Assert.Null(h.System.TypedText);
    }

    [Fact]
    public async Task A_prompt_always_offers_a_way_out()
    {
        var h = new Harness(s => s.AllowInputInjection = true);
        await h.Tap(Owner, "i:type");

        // Without this the chat is stuck with the prompt until it times out three
        // minutes later, and the cancel branch is unreachable code.
        Assert.Single(h.Telegram.Messages);
        Assert.True(h.Telegram.Messages[0].HasKeyboard);
    }

    [Fact]
    public async Task Cancelling_a_prompt_releases_the_next_message()
    {
        var h = new Harness(s => s.AllowInputInjection = true);
        await h.Tap(Owner, "i:type");
        await h.Tap(Owner, "x:cancel");

        await h.Text(Owner, "hello world");

        Assert.Null(h.System.TypedText);
    }

    // ---------- the callback contract ----------

    [Fact]
    public async Task A_tap_is_always_answered_even_when_the_panel_cannot_be_edited()
    {
        var h = new Harness();
        h.Telegram.EditFailure = new InvalidOperationException("message can't be edited");

        await h.Tap(Owner, "m:pwr");

        // Telegram spins the button until the query is answered.
        Assert.NotEmpty(h.Telegram.Answers);
        // And the screen still arrives, as a fresh panel.
        Assert.NotEmpty(h.Telegram.Messages);
    }

    [Fact]
    public async Task Navigating_edits_the_panel_rather_than_sending_a_new_message()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:pwr");

        Assert.Single(h.Telegram.Edits);
        Assert.Empty(h.Telegram.Messages);
    }

    // ---------- permission gates ----------

    [Fact]
    public async Task Shell_commands_are_refused_while_the_switch_is_off()
    {
        var h = new Harness();
        await h.Text(Owner, "/cmd dir");

        Assert.DoesNotContain(h.System.Calls, c => c.StartsWith("shell:", StringComparison.Ordinal));
        Assert.Contains(h.Telegram.Messages, m => m.Text.Contains("switched off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Shell_commands_run_once_the_switch_is_on()
    {
        var h = new Harness(s => s.AllowShellCommands = true);
        await h.Text(Owner, "/cmd dir");

        Assert.Contains("shell:dir", h.System.Calls);
    }

    [Fact]
    public async Task A_web_link_opens_without_file_access_but_a_local_path_does_not()
    {
        var h = new Harness();

        await h.Text(Owner, "/open https://example.com");
        Assert.Contains("open:https://example.com", h.System.Calls);

        h.System.Calls.Clear();
        await h.Text(Owner, @"/open C:\Windows\System32\cmd.exe");
        Assert.Empty(h.System.Calls);
    }

    [Fact]
    public async Task Typing_is_refused_while_its_switch_is_off()
    {
        var h = new Harness();
        await h.Text(Owner, "/type rm -rf");

        // Keystrokes into a focused terminal reach the same place /cmd does, so they
        // are behind their own switch rather than open by default.
        Assert.Null(h.System.TypedText);
        Assert.Contains(h.Telegram.Messages, m => m.Text.Contains("switched off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Typing_works_once_its_switch_is_on()
    {
        var h = new Harness(s => s.AllowInputInjection = true);
        await h.Text(Owner, "/type hello");

        Assert.Equal("hello", h.System.TypedText);
    }

    [Fact]
    public async Task The_typing_button_is_not_offered_while_its_switch_is_off()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:inp");

        var screen = BotMenu.Input(false);
        Assert.DoesNotContain(screen.Keyboard.InlineKeyboard.SelectMany(r => r), b => b.CallbackData == "i:type");
        Assert.Contains(BotMenu.Input(true).Keyboard.InlineKeyboard.SelectMany(r => r), b => b.CallbackData == "i:type");
    }

    [Fact]
    public async Task Tapping_a_stale_typing_button_is_still_refused()
    {
        // A panel sent before the switch was turned off is still in the chat.
        var h = new Harness();
        await h.Tap(Owner, "i:type");
        await h.Text(Owner, "hello");

        Assert.Null(h.System.TypedText);
    }

    [Fact]
    public async Task File_commands_are_refused_while_file_access_is_off()
    {
        var h = new Harness();
        await h.Text(Owner, @"/get C:\secret.txt");

        Assert.Empty(h.Telegram.Files);
    }

    // ---------- files ----------

    [Fact]
    public async Task Browsing_lists_a_folder_and_a_tap_walks_into_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "soulremote-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "inner"));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "hello");
        try
        {
            var h = new Harness(s => s.AllowFileAccess = true);
            await h.Text(Owner, "/files " + root);

            // The panel names the folder; its entries are the buttons.
            var listing = h.Telegram.Messages.Last();
            Assert.Contains(root, listing.Text, StringComparison.Ordinal);
            Assert.Contains(listing.Buttons, b => b.Contains("inner", StringComparison.Ordinal));
            Assert.Contains(listing.Buttons, b => b.Contains("notes.txt", StringComparison.Ordinal));

            // Buttons carry an index into that listing, because a real path does not fit
            // in Telegram's 64-byte callback_data.
            h.Telegram.Edits.Clear();
            await h.Tap(Owner, "f:0");
            Assert.Contains(h.Telegram.Edits, e => e.Text.Contains("inner", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Tapping_a_file_sends_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "soulremote-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "hello");
        try
        {
            var h = new Harness(s => s.AllowFileAccess = true);
            await h.Text(Owner, "/files " + root);
            await h.Tap(Owner, "f:0");

            Assert.Single(h.Telegram.Files);
            Assert.Equal("notes.txt", h.Telegram.Files[0].Name);
            Assert.False(h.Telegram.Files[0].IsPhoto);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task A_button_from_a_listing_the_chat_no_longer_has_is_refused()
    {
        var h = new Harness(s => s.AllowFileAccess = true);

        // No listing was ever taken for this chat, so the index refers to nothing.
        await h.Tap(Owner, "f:3");

        Assert.Empty(h.Telegram.Files);
        Assert.NotEmpty(h.Telegram.Messages);
    }

    [Fact]
    public async Task Browsing_is_refused_outright_while_file_access_is_off()
    {
        var h = new Harness();
        await h.Text(Owner, "/files C:\\");

        Assert.Contains(h.Telegram.Messages, m => m.Text.Contains("switched off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_document_sent_to_the_bot_is_saved_when_file_access_is_on()
    {
        var downloads = Path.Combine(Path.GetTempPath(), "soulremote-in-" + Guid.NewGuid().ToString("N"));
        try
        {
            var h = new Harness(s => { s.AllowFileAccess = true; s.DownloadFolder = downloads; });
            await h.Router.HandleUpdateAsync(new TgUpdate
            {
                UpdateId = 9,
                Message = new TgMessage
                {
                    MessageId = 1,
                    Chat = new TgChat { Id = Owner, Type = "private" },
                    Document = new TgDocument { FileId = "abc", FileName = "report.pdf", FileSize = 3 },
                },
            }, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(downloads, "report.pdf")));
        }
        finally
        {
            if (Directory.Exists(downloads)) Directory.Delete(downloads, true);
        }
    }

    [Fact]
    public async Task A_document_is_refused_while_file_access_is_off()
    {
        var h = new Harness();
        await h.Router.HandleUpdateAsync(new TgUpdate
        {
            UpdateId = 9,
            Message = new TgMessage
            {
                MessageId = 1,
                Chat = new TgChat { Id = Owner, Type = "private" },
                Document = new TgDocument { FileId = "abc", FileName = "report.pdf", FileSize = 3 },
            },
        }, CancellationToken.None);

        Assert.Contains(h.Telegram.Messages, m => m.Text.Contains("switched off", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- reports and captures ----------

    [Fact]
    public async Task A_report_that_throws_answers_with_the_error_rather_than_silence()
    {
        var h = new Harness { Info = { Failure = new IOException("the drive is not ready") } };
        await h.Text(Owner, "/disks");

        Assert.Single(h.Telegram.Messages);
        Assert.Contains("not ready", h.Telegram.Messages[0].Text);
    }

    [Fact]
    public async Task A_capture_too_large_for_a_photo_is_sent_as_a_file_instead_of_failing()
    {
        var h = new Harness();
        h.Screenshots.AsPhoto = false;

        await h.Text(Owner, "/screenshot");

        Assert.Single(h.Telegram.Files);
        Assert.False(h.Telegram.Files[0].IsPhoto);
    }

    [Fact]
    public async Task Asking_for_a_monitor_that_does_not_exist_reports_it()
    {
        var h = new Harness();
        h.Screenshots.ScreenCount = 1;

        await h.Text(Owner, "/screenshot 4");

        Assert.Empty(h.Telegram.Files);
        Assert.Contains(h.Telegram.Messages, m => m.Text.Contains("❌", StringComparison.Ordinal));
    }

    // ---------- language ----------

    [Fact]
    public async Task Switching_language_from_the_bot_persists_it_and_answers_in_the_new_one()
    {
        var h = new Harness();
        try
        {
            await h.Tap(Owner, "l:fa");

            Assert.Equal("fa", h.Settings.Current.Language);
            Assert.Equal(AppLanguage.Persian, Strings.Current);
            Assert.Contains(h.Telegram.Messages,
                m => m.Text == Strings.Get(AppLanguage.Persian, "bot.language.changed"));
        }
        finally
        {
            Strings.Use(AppLanguage.English);
        }
    }

    // ---------- rate limiting ----------

    [Fact]
    public async Task A_burst_from_one_chat_is_capped_without_locking_it_out_for_long()
    {
        var h = new Harness();
        for (var i = 0; i < 60; i++)
            await h.Text(Owner, "/lock");

        var duringBurst = h.System.Calls.Count;
        Assert.InRange(duringBurst, 1, 40);

        // The window is short: once it passes, the chat works normally again.
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.System.Calls.Clear();
        await h.Text(Owner, "/lock");
        Assert.Contains("lock", h.System.Calls);
    }

    // ---------- parsing ----------

    [Theory]
    [InlineData("/vol 50", "vol", "50")]
    [InlineData("/vol", "vol", null)]
    [InlineData("/VOL 50", "vol", "50")]
    [InlineData("/cmd echo hello world", "cmd", "echo hello world")]
    [InlineData("/lock@my_bot", "lock", null)]
    [InlineData("/say   spaced   ", "say", "spaced")]
    public void Commands_parse_into_a_verb_and_the_rest(string raw, string cmd, string? arg)
    {
        var (parsedCmd, parsedArg) = CommandRouter.ParseCommand(raw);
        Assert.Equal(cmd, parsedCmd);
        Assert.Equal(arg, parsedArg);
    }

    [Theory]
    [InlineData("m:home", "m", "home")]
    [InlineData("a:ss0", "a", "ss0")]
    [InlineData("home", "home", "")]
    [InlineData("f:", "f", "")]
    public void Callback_payloads_split_on_the_first_colon(string data, string kind, string value)
    {
        var (parsedKind, parsedValue) = CommandRouter.Split(data);
        Assert.Equal(kind, parsedKind);
        Assert.Equal(value, parsedValue);
    }
}
