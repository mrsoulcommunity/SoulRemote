using SoulRemote.Localization;
using SoulRemote.Models;
using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

/// <summary>
/// The bot's Settings section. These are behaviour tests in the same spirit as
/// <see cref="CommandRouterTests"/>: what a chat is allowed to change, what it has to
/// confirm first, and what actually reaches disk as a result.
///
/// The switch that runs through all of it is <c>AllowRemoteSettings</c>. It is the one
/// thing Telegram cannot reach, which is what makes it a way back when a paired chat
/// has been taken over — so most of what is checked here is that it holds on every
/// path, including the ones reached through a panel that was drawn while it was still
/// on.
/// </summary>
public sealed class SettingsSectionTests
{
    private const long Owner = 1001;
    private const long Phone = 2002;
    private const long Stranger = 3003;

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeSettings Settings { get; }
        public FakeTelegram Telegram { get; } = new();
        public FakeSystem System { get; } = new();
        public FakeScreenshots Screenshots { get; } = new();
        public FakeInfo Info { get; } = new();
        public FakeLog Log { get; } = new();
        public FakePcSettings Pc { get; } = new();
        public FakeStartup Startup { get; } = new();
        public CommandRouter Router { get; }

        /// <param name="headless">Build the router with no Windows half, as the core alone would run.</param>
        public Harness(Action<AppSettings>? configure = null, bool headless = false)
        {
            var settings = new AppSettings { AuthorizedChatIds = { Owner } };
            configure?.Invoke(settings);
            Settings = new FakeSettings(settings);
            Router = new CommandRouter(Settings, Telegram, System, Screenshots, Info, Log, Clock,
                pc: headless ? null : Pc,
                startup: headless ? null : Startup);
        }

        public Task Text(long chatId, string text) =>
            Router.HandleUpdateAsync(new TgUpdate
            {
                UpdateId = 1,
                Message = new TgMessage
                {
                    MessageId = 5,
                    Text = text,
                    Chat = new TgChat { Id = chatId, Type = "private" },
                    From = new TgUser { Id = chatId, FirstName = "Sara", Username = "sara_k" },
                },
            }, CancellationToken.None);

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

        /// <summary>Callback payloads on the panel last put in the chat — protocol, not captions.</summary>
        public IReadOnlyList<string> Panel =>
            Telegram.Edits.Count > 0 ? Telegram.Edits[^1].Payloads : Telegram.Messages[^1].Payloads;

        public string PanelText =>
            Telegram.Edits.Count > 0 ? Telegram.Edits[^1].Text : Telegram.Messages[^1].Text;

        public FakeTelegram.Answered LastAnswer => Telegram.Answers[^1];
    }

    public SettingsSectionTests() => Strings.Use(AppLanguage.English);

    /// <summary>Every write in the section, so the read-only switch can be swept over all of them.</summary>
    public static TheoryData<string> EveryWritePayload() => new()
    {
        "s:t.noti.1",
        "s:t.asb.1",
        "s:t.smin.1",
        "s:t.swin.1",
        "s:t.auc.0",
        "s:t.aui.1",
        "s:dlf.clear",
        "s:bri.75",
        "s:bt.0",
        "s:wfc.0",
        "s:pln." + FakePcSettings.SaverId,
        "c:p.shell.1",
        "y:p.shell.1",
        "c:cr.2002",
        "y:cr.2002",
        "c:wd",
        "y:wd",
        "i:poll",
        "i:logd",
        "i:dlf",
        "i:bri",
        "i:rn.2002",
        "s:t.pemj.1",
        "s:erm.0",
        "c:ecl",
        "y:ecl",
        "i:epk",
        "i:eadd",
        "i:eone.0",
    };

    // ---------- reachability ----------

    [Fact]
    public async Task The_home_panel_offers_settings_and_the_screen_opens()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:home");
        Assert.Contains("m:set", h.Panel);

        await h.Tap(Owner, "m:set");
        Assert.Contains("m:sper", h.Panel);
        Assert.Contains("m:swin", h.Panel);
    }

    [Fact]
    public async Task Slash_settings_opens_the_same_screen()
    {
        var h = new Harness();
        await h.Text(Owner, "/settings");
        Assert.Contains("m:sper", h.Panel);
    }

    [Fact]
    public async Task An_unpaired_chat_cannot_open_settings_or_change_anything()
    {
        var h = new Harness();
        await h.Tap(Stranger, "m:set");
        await h.Tap(Stranger, "s:t.noti.1");

        Assert.Equal(0, h.Settings.SaveCount);
        Assert.All(h.Telegram.Answers, a => Assert.True(a.Alert));
    }

    [Theory]
    [InlineData("m:set")]
    [InlineData("m:sper")]
    [InlineData("m:sst")]
    [InlineData("m:sbot")]
    [InlineData("m:scht")]
    [InlineData("m:swin")]
    [InlineData("m:spln")]
    [InlineData("m:sbri")]
    [InlineData("m:swif")]
    [InlineData("m:sblu")]
    public async Task Every_screen_renders_and_answers_the_tap(string payload)
    {
        var h = new Harness();
        await h.Tap(Owner, payload);

        // A callback query that is never answered leaves the button spinning forever,
        // which is the failure a new dispatcher arm causes first.
        Assert.NotEmpty(h.Telegram.Answers);
        Assert.NotEmpty(h.PanelText);
        Assert.Contains(h.Panel, p => p.StartsWith("m:", StringComparison.Ordinal));
    }

    // ---------- the three security switches ----------

    [Fact]
    public async Task Turning_on_shell_commands_asks_first_and_saves_nothing_yet()
    {
        var h = new Harness();
        await h.Tap(Owner, "c:p.shell.1");

        Assert.Equal(0, h.Settings.SaveCount);
        Assert.False(h.Settings.Current.AllowShellCommands);
        Assert.Contains("y:p.shell.1", h.Panel);
    }

    [Fact]
    public async Task Backing_out_of_a_permission_returns_to_permissions_not_to_power()
    {
        // The regression the cancel-target overload exists to prevent: Confirm used to
        // hardcode "m:pwr", so declining "run commands?" landed you on Shut down.
        var h = new Harness();
        await h.Tap(Owner, "c:p.shell.1");

        Assert.Contains("m:sper", h.Panel);
        Assert.DoesNotContain("m:pwr", h.Panel);
    }

    [Fact]
    public async Task Confirming_applies_the_permission_and_the_processes_screen_follows()
    {
        var h = new Harness();
        await h.Tap(Owner, "y:p.shell.1");
        Assert.True(h.Settings.Current.AllowShellCommands);

        await h.Tap(Owner, "m:prc");
        Assert.Contains("i:cmd", h.Panel);
    }

    [Fact]
    public async Task A_permission_payload_sets_a_state_rather_than_flipping_one()
    {
        // The payload carries the state it will set, so a panel left open in a chat and
        // tapped twice lands where its caption promised instead of toggling past it.
        var h = new Harness();
        await h.Tap(Owner, "y:p.file.1");
        await h.Tap(Owner, "y:p.file.1");

        Assert.True(h.Settings.Current.AllowFileAccess);
    }

    [Fact]
    public async Task Withdrawing_a_permission_is_confirmed_too()
    {
        var h = new Harness(s => s.AllowInputInjection = true);
        await h.Tap(Owner, "c:p.inp.0");
        Assert.Equal(0, h.Settings.SaveCount);

        await h.Tap(Owner, "y:p.inp.0");
        Assert.False(h.Settings.Current.AllowInputInjection);
    }

    // ---------- read-only ----------

    [Theory]
    [MemberData(nameof(EveryWritePayload))]
    public async Task Nothing_writes_while_remote_settings_are_off(string payload)
    {
        var h = new Harness(s =>
        {
            s.AllowRemoteSettings = false;
            s.AuthorizedChatIds.Add(Phone);
        });

        await h.Tap(Owner, payload);

        Assert.Equal(0, h.Settings.SaveCount);
        Assert.Empty(h.Pc.Calls);
        Assert.Empty(h.Startup.Calls);
        Assert.True(h.LastAnswer.Alert);
    }

    [Fact]
    public async Task A_read_only_screen_still_renders_but_offers_only_the_way_back()
    {
        var h = new Harness(s => s.AllowRemoteSettings = false);
        await h.Tap(Owner, "m:sper");

        // Seeing how your PC is configured is not a write, so the screen is still shown.
        Assert.Contains(Strings.Get("bot.set.readonly"), h.PanelText, StringComparison.Ordinal);
        Assert.Equal(new[] { "m:set" }, h.Panel);
    }

    [Fact]
    public async Task Navigation_keeps_working_while_settings_are_read_only()
    {
        var h = new Harness(s => s.AllowRemoteSettings = false);
        await h.Tap(Owner, "m:set");
        Assert.Contains("m:sbot", h.Panel);
    }

    [Fact]
    public async Task A_prompt_opened_before_the_switch_flipped_is_refused_when_it_is_answered()
    {
        // Prompts live for three minutes, which is long enough for the desktop to close
        // the door while one is waiting for its answer.
        var h = new Harness();
        await h.Tap(Owner, "i:poll");

        var locked = h.Settings.Current.Clone();
        locked.AllowRemoteSettings = false;
        h.Settings.Save(locked);
        var savesBefore = h.Settings.SaveCount;

        await h.Text(Owner, "40");

        Assert.Equal(savesBefore, h.Settings.SaveCount);
        Assert.Equal(25, h.Settings.Current.PollTimeoutSeconds);
    }

    [Fact]
    public async Task The_switch_itself_cannot_be_reached_from_telegram()
    {
        // If the bot could turn this back on, turning it off would protect nothing.
        var h = new Harness(s => s.AllowRemoteSettings = false);
        foreach (var guess in new[] { "s:t.rem.1", "s:t.remote.1", "y:p.rem.1", "s:rem.1" })
            await h.Tap(Owner, guess);

        Assert.False(h.Settings.Current.AllowRemoteSettings);
    }

    // ---------- startup and preferences ----------

    [Fact]
    public async Task Start_with_windows_writes_the_registry_entry_and_the_setting()
    {
        var h = new Harness();
        await h.Tap(Owner, "s:t.swin.1");

        Assert.True(h.Startup.Enabled);
        Assert.True(h.Settings.Current.StartWithWindows);
    }

    [Fact]
    public async Task A_failed_save_puts_the_registry_entry_back()
    {
        // Otherwise the machine would launch an app whose own settings say it should not.
        var h = new Harness();
        h.Settings.FailSaves = true;
        await h.Tap(Owner, "s:t.swin.1");

        Assert.False(h.Startup.Enabled);
        Assert.True(h.LastAnswer.Alert);
    }

    [Fact]
    public async Task Start_with_windows_is_not_offered_when_nothing_can_write_it()
    {
        var h = new Harness(headless: true);
        await h.Tap(Owner, "m:sst");

        Assert.DoesNotContain(h.Panel, p => p.StartsWith("s:t.swin", StringComparison.Ordinal));
        Assert.Contains("s:t.noti", string.Join(" ", h.Panel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_off_update_checks_also_stops_unattended_installs()
    {
        // A screen showing "install by itself" on while nothing checks would be untrue.
        var h = new Harness(s => { s.AutoCheckUpdates = true; s.AutoInstallUpdates = true; });
        await h.Tap(Owner, "s:t.auc.0");

        Assert.False(h.Settings.Current.AutoCheckUpdates);
        Assert.False(h.Settings.Current.AutoInstallUpdates);
    }

    [Fact]
    public async Task Turning_on_unattended_installs_turns_checking_on_with_it()
    {
        var h = new Harness(s => { s.AutoCheckUpdates = false; s.AutoInstallUpdates = false; });
        await h.Tap(Owner, "s:t.aui.1");

        Assert.True(h.Settings.Current.AutoInstallUpdates);
        Assert.True(h.Settings.Current.AutoCheckUpdates);
    }

    [Theory]
    [InlineData("40", 40)]
    [InlineData("999", 50)]
    [InlineData("1", 5)]
    public async Task A_poll_timeout_is_clamped_to_what_the_settings_allow(string sent, int expected)
    {
        var h = new Harness();
        await h.Tap(Owner, "i:poll");
        await h.Text(Owner, sent);

        Assert.Equal(expected, h.Settings.Current.PollTimeoutSeconds);
    }

    [Fact]
    public async Task A_poll_timeout_that_is_not_a_number_changes_nothing_and_says_so()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:poll");
        await h.Text(Owner, "soon");

        Assert.Equal(25, h.Settings.Current.PollTimeoutSeconds);
        Assert.Contains(h.Telegram.Messages, m => m.Text.Contains("not a whole number", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zero_log_retention_reads_as_keeping_them_rather_than_as_none()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:logd");
        await h.Text(Owner, "0");

        Assert.Equal(0, h.Settings.Current.LogRetentionDays);
        Assert.Contains(h.Telegram.Messages,
            m => m.Text.Contains("indefinitely", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_folder_that_is_not_there_is_refused()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:dlf");
        await h.Text(Owner, @"Z:\nowhere\at\all");

        Assert.Equal(string.Empty, h.Settings.Current.DownloadFolder);
        Assert.Contains(h.Telegram.Messages, m => m.Text.Contains("no folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_folder_that_exists_is_stored_and_can_be_cleared()
    {
        var h = new Harness();
        await h.Tap(Owner, "i:dlf");
        await h.Text(Owner, Path.GetTempPath());
        Assert.Equal(Path.GetTempPath(), h.Settings.Current.DownloadFolder);

        await h.Tap(Owner, "s:dlf.clear");
        Assert.Equal(string.Empty, h.Settings.Current.DownloadFolder);
    }

    // ---------- paired chats ----------

    [Fact]
    public async Task A_chat_can_be_renamed()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Add(Phone));
        await h.Tap(Owner, "i:rn." + Phone);
        await h.Text(Owner, "  Sara's phone  ");

        Assert.Equal("Sara's phone", h.Settings.Current.NameFor(Phone));
    }

    [Fact]
    public async Task A_blank_name_is_refused_and_nothing_is_saved()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Add(Phone));
        await h.Tap(Owner, "i:rn." + Phone);
        var before = h.Settings.SaveCount;
        await h.Text(Owner, "   ");

        Assert.Equal(before, h.Settings.SaveCount);
    }

    [Fact]
    public async Task Revoking_a_chat_removes_its_access_and_drops_what_the_router_held()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Add(Phone));
        // Something outstanding for that chat, which must not survive it losing access.
        await h.Tap(Phone, "i:vol");

        await h.Tap(Owner, "y:cr." + Phone);

        Assert.DoesNotContain(Phone, h.Settings.Current.AuthorizedChatIds);

        // The old prompt must not claim this chat's next message, and the chat is a
        // stranger now anyway.
        await h.Text(Phone, "50");
        Assert.DoesNotContain(h.System.Calls, c => c == "setvolume");
    }

    [Fact]
    public async Task Removing_the_only_paired_chat_is_refused()
    {
        // It would leave the PC reachable only from the desktop app.
        var h = new Harness();
        await h.Tap(Owner, "y:cr." + Owner);

        Assert.Contains(Owner, h.Settings.Current.AuthorizedChatIds);
        Assert.True(h.LastAnswer.Alert);
    }

    [Fact]
    public async Task Revoking_the_chat_you_are_in_is_worded_differently()
    {
        var h = new Harness(s => s.AuthorizedChatIds.Add(Phone));
        await h.Tap(Owner, "c:cr." + Owner);
        var self = h.PanelText;

        var h2 = new Harness(s => s.AuthorizedChatIds.Add(Phone));
        await h2.Tap(Owner, "c:cr." + Phone);

        Assert.NotEqual(self, h2.PanelText);
        Assert.Contains("lose access", self, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chat_that_is_already_gone_says_so_rather_than_throwing()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:sch.987654");
        Assert.Contains(Strings.Get("bot.set.chat.gone"), h.PanelText, StringComparison.Ordinal);

        await h.Tap(Owner, "y:cr.987654");
        Assert.True(h.LastAnswer.Alert);
    }

    // ---------- Windows settings ----------

    [Fact]
    public async Task A_power_plan_is_switched_by_its_guid()
    {
        var h = new Harness();
        await h.Tap(Owner, "s:pln." + FakePcSettings.SaverId);

        Assert.Contains("setplan:" + FakePcSettings.SaverId, h.Pc.Calls);
    }

    [Fact]
    public async Task A_power_plan_payload_that_is_not_a_guid_never_reaches_the_machine()
    {
        var h = new Harness();
        await h.Tap(Owner, "s:pln.; shutdown /s");

        Assert.DoesNotContain(h.Pc.Calls, c => c.StartsWith("setplan", StringComparison.Ordinal));
        Assert.True(h.LastAnswer.Alert);
    }

    [Fact]
    public async Task The_active_power_plan_is_not_offered_as_something_to_switch_to()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:spln");

        Assert.DoesNotContain("s:pln." + FakePcSettings.BalancedId, h.Panel);
        Assert.Contains("s:pln." + FakePcSettings.SaverId, h.Panel);
    }

    [Fact]
    public async Task Brightness_can_be_set_by_button_or_by_value()
    {
        var h = new Harness();
        await h.Tap(Owner, "s:bri.75");
        Assert.Contains("setbrightness:75", h.Pc.Calls);

        await h.Tap(Owner, "i:bri");
        await h.Text(Owner, "30");
        Assert.Contains("setbrightness:30", h.Pc.Calls);
    }

    [Fact]
    public async Task A_machine_with_no_controllable_panel_says_so_and_offers_no_levels()
    {
        var h = new Harness();
        h.Pc.Brightness = new BrightnessState(false, null);
        await h.Tap(Owner, "m:sbri");

        Assert.Contains(Strings.Get("bot.set.bri.unsupported"), h.PanelText, StringComparison.Ordinal);
        Assert.Equal(new[] { "m:swin" }, h.Panel);
    }

    [Fact]
    public async Task Disconnecting_wifi_is_confirmed_and_the_warning_names_the_consequence()
    {
        var h = new Harness();
        await h.Tap(Owner, "c:wd");

        Assert.DoesNotContain("wifidisconnect", h.Pc.Calls);
        Assert.Contains("cuts this bot off", h.PanelText, StringComparison.Ordinal);
        Assert.Contains("y:wd", h.Panel);

        await h.Tap(Owner, "y:wd");
        Assert.Contains("wifidisconnect", h.Pc.Calls);
    }

    [Fact]
    public async Task Connecting_to_a_saved_network_goes_through_the_list_the_screen_showed()
    {
        var h = new Harness();
        await h.Tap(Owner, "m:swif");
        await h.Tap(Owner, "s:wfc.1");

        // Index 1 is the non-ASCII SSID, which is exactly why it is not in the payload.
        Assert.Contains("wificonnect:Café Wi-Fi ☕", h.Pc.Calls);
    }

    [Fact]
    public async Task An_index_from_a_list_this_chat_never_saw_is_refused()
    {
        var h = new Harness();
        await h.Tap(Owner, "s:wfc.4");

        Assert.DoesNotContain(h.Pc.Calls, c => c.StartsWith("wificonnect", StringComparison.Ordinal));
        Assert.True(h.LastAnswer.Alert);
    }

    [Fact]
    public async Task Bluetooth_offers_the_state_it_is_not_already_in()
    {
        var h = new Harness();
        h.Pc.Bluetooth = RadioPower.On;
        await h.Tap(Owner, "m:sblu");
        Assert.Contains("s:bt.0", h.Panel);

        await h.Tap(Owner, "s:bt.0");
        Assert.Contains("setbluetooth:0", h.Pc.Calls);
    }

    [Fact]
    public async Task A_machine_with_no_bluetooth_radio_renders_rather_than_throwing()
    {
        var h = new Harness();
        h.Pc.Bluetooth = RadioPower.Unavailable;
        await h.Tap(Owner, "m:sblu");

        Assert.Contains(Strings.Get("bot.set.bt.none"), h.PanelText, StringComparison.Ordinal);
        Assert.Equal(new[] { "m:swin" }, h.Panel);
    }

    [Fact]
    public async Task A_subsystem_that_throws_leaves_a_screen_with_a_way_back()
    {
        var h = new Harness();
        h.Pc.Failure = new InvalidOperationException("WMI is not having it");
        await h.Tap(Owner, "m:sbri");

        Assert.Contains("WMI is not having it", h.PanelText, StringComparison.Ordinal);
        Assert.Contains("m:swin", h.Panel);
    }

    [Fact]
    public async Task Without_a_windows_half_the_section_says_so_instead_of_failing()
    {
        var h = new Harness(headless: true);
        await h.Tap(Owner, "m:swin");
        Assert.Contains(Strings.Get("bot.set.win.unavailable"), h.PanelText, StringComparison.Ordinal);

        await h.Tap(Owner, "m:sbri");
        Assert.Contains(Strings.Get("bot.set.win.unavailable"), h.PanelText, StringComparison.Ordinal);

        await h.Tap(Owner, "s:bri.50");
        Assert.True(h.LastAnswer.Alert);
    }

    // ---------- saving ----------

    [Fact]
    public async Task A_save_that_does_not_reach_disk_is_reported_rather_than_assumed()
    {
        var h = new Harness();
        h.Settings.FailSaves = true;
        await h.Tap(Owner, "s:t.noti.0");

        Assert.True(h.LastAnswer.Alert);
        Assert.True(h.Settings.Current.NotifyOnStartup);
    }
}
