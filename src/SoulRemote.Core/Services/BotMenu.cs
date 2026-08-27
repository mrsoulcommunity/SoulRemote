using System.Globalization;
using System.Text;
using SoulRemote.Localization;
using SoulRemote.Models;

namespace SoulRemote.Services;

/// <summary>
/// Every screen the Telegram bot can show, and the keyboards that reach them.
///
/// The bot is driven by buttons, not typed commands: a tap edits the current
/// message into the next screen, so one message stays the control panel instead
/// of the chat filling with replies. Callback payloads are kept short because
/// Telegram caps callback_data at 64 bytes.
///
/// Every caption comes from the string catalogue, so the same screens render in
/// English or Persian. Callback payloads never do — they are protocol, not prose,
/// and a tap made in one language must still work after the language changes.
/// </summary>
public static class BotMenu
{
    // ---- persistent shortcut bar under the composer ----
    // The bar arrives as ordinary text, so the router matches on these captions.
    // They are language-dependent, which is why the router asks for every variant
    // rather than comparing against the current language only.

    public static string BarMenu => T("bot.bar.menu");
    public static string BarShot => T("bot.bar.shot");
    public static string BarLock => T("bot.bar.lock");
    public static string BarPower => T("bot.bar.power");

    /// <summary>
    /// The shortcut-bar captions in every language. A user who switches language
    /// still has the old bar pinned in Telegram until it is replaced, so taps on it
    /// have to keep working.
    /// </summary>
    public static IEnumerable<(string Caption, string Action)> ShortcutCaptions()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            yield return (Strings.Get(language, "bot.bar.menu"), "menu");
            yield return (Strings.Get(language, "bot.bar.shot"), "shot");
            yield return (Strings.Get(language, "bot.bar.lock"), "lock");
            yield return (Strings.Get(language, "bot.bar.power"), "power");
        }
    }

    public static TgReplyKeyboardMarkup ShortcutBar() => new()
    {
        ResizeKeyboard = true,
        IsPersistent = true,
        Placeholder = T("bot.bar.placeholder"),
        Keyboard = new()
        {
            new() { new TgKeyboardButton(BarMenu), new TgKeyboardButton(BarShot) },
            new() { new TgKeyboardButton(BarLock), new TgKeyboardButton(BarPower) },
        },
    };

    /// <summary>A rendered screen: what to say and which buttons to offer.</summary>
    public readonly record struct Screen(string Text, TgInlineKeyboardMarkup Keyboard);

    /// <summary>
    /// Home doubles as the status panel: opening the bot should answer "how is my PC?"
    /// without a further tap. That is also what gives Refresh something to do, and the
    /// changing timestamp keeps Telegram from rejecting the edit as unmodified.
    /// </summary>
    public static Screen Home(string machine, string status, bool fileAccess) =>
        new(
            $"🎛 <b>{TextUtil.Html(machine)}</b>\n{status}",
            Keyboard(BuildHomeRows(fileAccess)));

    private static List<List<TgInlineKeyboardButton>> BuildHomeRows(bool fileAccess)
    {
        var rows = new List<List<TgInlineKeyboardButton>>
        {
            Row((T("bot.menu.capture"), "m:cap"), (T("bot.menu.power"), "m:pwr")),
            Row((T("bot.menu.audio"), "m:aud"), (T("bot.menu.input"), "m:inp")),
            Row((T("bot.menu.system"), "m:sys"), (T("bot.menu.processes"), "m:prc")),
        };
        if (fileAccess)
            rows.Add(Row((T("bot.menu.files"), "m:fil")));
        rows.Add(Row((T("bot.menu.settings"), "m:set")));
        rows.Add(Row(
            (T("bot.menu.refresh"), "m:home"),
            (Strings.Format("bot.menu.language", Other().NativeName()), "l:" + Other().Tag())));
        return rows;
    }

    /// <summary>The language the button offers to switch to — the one you are not using.</summary>
    public static AppLanguage Other() =>
        Strings.Current == AppLanguage.Persian ? AppLanguage.English : AppLanguage.Persian;

    public static Screen Capture(int screenCount)
    {
        var rows = new List<List<TgInlineKeyboardButton>>
        {
            Row((T("bot.capture.desktop"), "a:ss")),
        };
        if (screenCount > 1)
        {
            var perScreen = new List<TgInlineKeyboardButton>();
            for (var i = 0; i < Math.Min(screenCount, 4); i++)
                perScreen.Add(new TgInlineKeyboardButton(Strings.Format("bot.capture.monitor", i + 1), $"a:ss{i}"));
            rows.Add(perScreen);
        }
        rows.Add(Row((T("bot.menu.back"), "m:home")));
        return new Screen(
            screenCount > 1
                ? T("bot.capture.title") + "\n" + Strings.Format("bot.capture.displays", screenCount)
                : T("bot.capture.title"),
            new TgInlineKeyboardMarkup { InlineKeyboard = rows });
    }

    public static Screen Power() => new(
        T("bot.power.title") + "\n" + T("bot.power.subtitle"),
        Keyboard(
            Row((T("bot.power.lock"), "a:lock"), (T("bot.power.sleep"), "a:sleep")),
            Row((T("bot.power.display"), "a:mon"), (T("bot.power.hibernate"), "c:hb")),
            Row((T("bot.power.signout"), "c:lo"), (T("bot.power.restart"), "c:rs")),
            Row((T("bot.power.shutdown"), "c:sd")),
            Row((T("bot.power.abort"), "a:abort")),
            Row((T("bot.menu.back"), "m:home"))));

    public static Screen Audio() => new(
        T("bot.audio.title"),
        Keyboard(
            Row((T("bot.audio.down"), "a:vdn"), (T("bot.audio.mute"), "a:mute"), (T("bot.audio.up"), "a:vup")),
            Row((T("bot.audio.prev"), "a:prev"), (T("bot.audio.play"), "a:play"), (T("bot.audio.next"), "a:next")),
            Row((T("bot.audio.setlevel"), "i:vol")),
            Row((T("bot.menu.back"), "m:home"))));

    public static Screen Input(bool typingAllowed)
    {
        var rows = new List<List<TgInlineKeyboardButton>>
        {
            Row((T("bot.input.readclip"), "a:clip")),
        };
        rows.Add(typingAllowed
            ? Row((T("bot.input.setclip"), "i:clip"), (T("bot.input.type"), "i:type"))
            : Row((T("bot.input.setclip"), "i:clip")));
        rows.Add(Row((T("bot.input.open"), "i:open"), (T("bot.input.speak"), "i:say")));
        rows.Add(Row((T("bot.menu.back"), "m:home")));

        var text = T("bot.input.title") + "\n" + T("bot.input.subtitle");
        if (!typingAllowed)
            text += "\n" + T("bot.input.typingoff");
        return new Screen(text, new TgInlineKeyboardMarkup { InlineKeyboard = rows });
    }

    public static Screen System() => new(
        T("bot.system.title"),
        Keyboard(
            Row((T("bot.system.overview"), "a:sys"), (T("bot.system.disks"), "a:disk")),
            Row((T("bot.system.battery"), "a:bat"), (T("bot.system.network"), "a:net")),
            Row((T("bot.menu.back"), "m:home"))));

    public static Screen Processes(bool shellAllowed)
    {
        var rows = new List<List<TgInlineKeyboardButton>>
        {
            Row((T("bot.proc.top"), "a:ps")),
            Row((T("bot.proc.kill"), "i:kill")),
        };
        if (shellAllowed)
            rows.Add(Row((T("bot.proc.run"), "i:cmd")));
        rows.Add(Row((T("bot.menu.back"), "m:home")));
        return new Screen(
            shellAllowed ? T("bot.proc.title") : T("bot.proc.title") + "\n" + T("bot.proc.shelloff"),
            new TgInlineKeyboardMarkup { InlineKeyboard = rows });
    }

    public static Screen Files(bool fileAccess)
    {
        var rows = new List<List<TgInlineKeyboardButton>>();
        if (fileAccess)
        {
            rows.Add(Row((T("bot.files.browse"), "i:path")));
            rows.Add(Row((T("bot.files.send"), "i:get")));
        }
        rows.Add(Row((T("bot.menu.back"), "m:home")));
        return new Screen(
            fileAccess ? T("bot.files.title") : T("bot.files.title") + "\n" + T("bot.files.off"),
            new TgInlineKeyboardMarkup { InlineKeyboard = rows });
    }

    /// <param name="cancel">
    /// Where "No" goes. Defaulted to the power screen because that is where every
    /// confirmation used to come from; the settings screens pass their own, or
    /// backing out of "turn on shell commands" would land you on Shut down.
    /// </param>
    /// <param name="note">An extra warning line, for a confirmation that needs one.</param>
    public static Screen Confirm(string action, string question, string cancel = "m:pwr", string? note = null) => new(
        $"⚠️ <b>{TextUtil.Html(question)}</b>\n{T("bot.confirm.warning")}"
            + (string.IsNullOrEmpty(note) ? string.Empty : "\n\n" + note),
        Keyboard(
            Row((T("bot.confirm.yes"), $"y:{action}"), (T("bot.confirm.no"), cancel))));

    // ============================================================
    // Settings
    //
    // Every screen below takes `writable`. When it is false the desktop app has
    // switched off remote settings, and the screens still render — you should be
    // able to see how your PC is configured from your phone even when you may not
    // change it — but the state moves out of the buttons and into the text, and
    // only the way back remains. That is the same shape Files and Processes already
    // use when their permission is off, rather than a second idea of "disabled".
    // ============================================================

    public static Screen Settings(bool writable)
    {
        var text = T("bot.set.title") + "\n" + T("bot.set.subtitle");
        if (!writable)
            text += "\n\n" + T("bot.set.readonly");
        return new Screen(text, Keyboard(
            Row((T("bot.set.perms"), "m:sper"), (T("bot.set.startup"), "m:sst")),
            Row((T("bot.set.prefs"), "m:sbot"), (T("bot.set.chats"), "m:scht")),
            Row((T("bot.set.emoji"), "m:semj"), (T("bot.set.windows"), "m:swin")),
            Row((T("bot.menu.back"), "m:home"))));
    }

    // ---- Premium emoji ----

    /// <summary>How many emoji a page of the converted-emoji list shows.</summary>
    public const int EmojiPageSize = 12;

    /// <summary>
    /// The premium-emoji panel.
    ///
    /// It leads with what was actually achieved — "38 of 64 converted" — rather than
    /// with the switch, because that number is the only honest answer to the question
    /// someone opens this screen with. A pack covers whatever it covers; the emoji it
    /// has no version of stay as they were, and saying so here is better than leaving
    /// the user to notice.
    /// </summary>
    public static Screen PremiumEmoji(AppSettings s, PremiumEmojiState state, bool writable)
    {
        var mapped = EmojiCatalog.ConvertedCount(s.PremiumEmoji);
        var text = new StringBuilder(T("bot.set.emoji.title"));
        text.Append('\n').Append(Strings.Format("bot.set.emoji.count", mapped, EmojiCatalog.Count));

        if (s.PremiumEmojiPack is { Length: > 0 } pack)
            text.Append('\n').Append(Strings.Format("bot.set.emoji.pack", TextUtil.Html(pack)));

        // The entitlement line only appears once there is something to report: telling
        // someone their premium emoji might not be allowed before they have set any is
        // a warning about a problem they do not have yet.
        if (mapped > 0)
        {
            text.Append('\n').Append(state switch
            {
                PremiumEmojiState.Working => T("bot.set.emoji.working"),
                PremiumEmojiState.Refused => T("bot.set.emoji.refused"),
                _ => T("bot.set.emoji.untested"),
            });
        }

        if (!writable)
        {
            text.Append("\n\n").Append(T("bot.set.readonly"));
            return new Screen(text.ToString(), Keyboard(Row((T("bot.menu.back"), "m:set"))));
        }

        var rows = new List<List<TgInlineKeyboardButton>>();
        Toggle(rows, text, "bot.set.emoji.use", s.UsePremiumEmoji, "s:t.pemj", true);
        rows.Add(Row((T("bot.set.emoji.import"), "i:epk")));
        rows.Add(Row((T("bot.set.emoji.add"), "i:eadd")));
        rows.Add(Row((T("bot.set.emoji.list"), "m:seml.0")));
        if (mapped > 0)
            rows.Add(Row((T("bot.set.emoji.clear"), "c:ecl")));
        rows.Add(Row((T("bot.menu.back"), "m:set")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    /// <summary>
    /// One page of every emoji the bot uses, each showing whether it has a premium
    /// stand-in. Tapping a converted one clears it; tapping a plain one asks for the
    /// premium version of that particular emoji.
    ///
    /// Paged because Telegram will not take an unbounded keyboard and a wall of sixty
    /// buttons is not a list anyone reads. The payload carries an index into the
    /// catalogue rather than the emoji itself: the catalogue is built the same way
    /// every run, and an emoji is several bytes of a sixty-four byte budget.
    /// </summary>
    public static Screen PremiumEmojiList(AppSettings s, int page, bool writable)
    {
        var all = EmojiCatalog.All;
        var pages = Math.Max(1, (all.Count + EmojiPageSize - 1) / EmojiPageSize);
        page = Math.Clamp(page, 0, pages - 1);

        var text = new StringBuilder(T("bot.set.emoji.list.title"));
        text.Append('\n').Append(Strings.Format("bot.set.emoji.list.page", page + 1, pages));
        if (writable)
            text.Append('\n').Append(T("bot.set.emoji.list.hint"));
        else
            text.Append("\n\n").Append(T("bot.set.readonly"));

        var rows = new List<List<TgInlineKeyboardButton>>();
        var start = page * EmojiPageSize;
        var end = Math.Min(start + EmojiPageSize, all.Count);

        for (var i = start; i < end; i += 2)
        {
            var row = new List<TgInlineKeyboardButton>();
            for (var j = i; j < Math.Min(i + 2, end); j++)
            {
                var use = all[j];
                var on = s.PremiumEmoji.ContainsKey(use.Emoji);
                var caption = (on ? "✅ " : "⬜ ") + use.Emoji + " "
                              + TextUtil.Clip(EmojiCatalog.LabelFor(use.Emoji), 18);
                if (writable)
                {
                    // A button caption is plain text to Telegram, so it goes as it is.
                    row.Add(new TgInlineKeyboardButton(caption,
                        (on ? "s:erm." : "i:eone.") + j.ToString(CultureInfo.InvariantCulture)));
                }
                else
                {
                    // The same caption in the message body is not: it is parsed as HTML,
                    // and a label like "Startup & notifications" carries an ampersand.
                    text.Append('\n').Append(TextUtil.Html(caption));
                }
            }
            if (row.Count > 0)
                rows.Add(row);
        }

        var nav = new List<TgInlineKeyboardButton>();
        if (page > 0)
            nav.Add(new TgInlineKeyboardButton(T("bot.set.emoji.prev"), $"m:seml.{page - 1}"));
        if (page < pages - 1)
            nav.Add(new TgInlineKeyboardButton(T("bot.set.emoji.next"), $"m:seml.{page + 1}"));
        if (nav.Count > 0)
            rows.Add(nav);

        rows.Add(Row((T("bot.menu.back"), "m:semj")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    public static Screen Permissions(AppSettings s, bool writable)
    {
        var rows = new List<List<TgInlineKeyboardButton>>();
        var text = new StringBuilder(T("bot.set.perm.title"));
        text.Append('\n').Append(T("bot.set.perm.warning"));
        if (!writable)
            text.Append("\n\n").Append(T("bot.set.readonly"));

        Toggle(rows, text, "bot.set.perm.shell", s.AllowShellCommands, "c:p.shell", writable);
        Toggle(rows, text, "bot.set.perm.files", s.AllowFileAccess, "c:p.file", writable);
        Toggle(rows, text, "bot.set.perm.typing", s.AllowInputInjection, "c:p.inp", writable);

        rows.Add(Row((T("bot.menu.back"), "m:set")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    public static Screen Startup(AppSettings s, bool writable, bool startupManaged)
    {
        var rows = new List<List<TgInlineKeyboardButton>>();
        var text = new StringBuilder(T("bot.set.startup.title"));
        if (!writable)
            text.Append("\n\n").Append(T("bot.set.readonly"));

        // Start-with-Windows is a registry entry as well as a stored flag. Without
        // something to write that entry, offering the button would save a setting
        // the machine does not act on.
        Toggle(rows, text, "bot.set.startup.startwin", s.StartWithWindows, "s:t.swin", writable && startupManaged);
        if (writable && !startupManaged)
            text.Append("\n<i>").Append(TextUtil.Html(T("bot.set.startup.unmanaged"))).Append("</i>");

        Toggle(rows, text, "bot.set.startup.autobot", s.AutoStartBot, "s:t.asb", writable);
        Toggle(rows, text, "bot.set.startup.startmin", s.StartMinimized, "s:t.smin", writable);
        Toggle(rows, text, "bot.set.startup.notify", s.NotifyOnStartup, "s:t.noti", writable);

        rows.Add(Row((T("bot.menu.back"), "m:set")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    public static Screen BotPrefs(AppSettings s, bool writable)
    {
        var rows = new List<List<TgInlineKeyboardButton>>();
        var text = new StringBuilder(T("bot.set.pref.title"));
        if (!writable)
            text.Append("\n\n").Append(T("bot.set.readonly"));

        var poll = Strings.Format("bot.set.pref.poll", s.PollTimeoutSeconds);
        var logs = s.LogRetentionDays == 0
            ? T("bot.set.pref.logs.forever")
            : Strings.Format("bot.set.pref.logs", s.LogRetentionDays);
        var folder = Strings.Format("bot.set.pref.folder",
            TextUtil.Html(s.DownloadFolder is { Length: > 0 } d ? d : T("bot.set.pref.folder.default")));

        if (writable)
        {
            rows.Add(Row((poll, "i:poll")));
            rows.Add(Row((logs, "i:logd")));
            rows.Add(Row((T("bot.set.pref.folder.set"), "i:dlf")));
            if (s.DownloadFolder is { Length: > 0 })
                rows.Add(Row((T("bot.set.pref.folder.reset"), "s:dlf.clear")));
        }
        else
        {
            text.Append('\n').Append(poll).Append('\n').Append(logs);
        }
        // The folder is a path, which does not fit a button caption, so it is stated
        // in the text either way and the button only opens the prompt.
        text.Append('\n').Append(folder);

        Toggle(rows, text, "bot.set.pref.autocheck", s.AutoCheckUpdates, "s:t.auc", writable);
        Toggle(rows, text, "bot.set.pref.autoinstall", s.AutoInstallUpdates, "s:t.aui", writable);

        rows.Add(Row(
            (Strings.Format("bot.menu.language", Other().NativeName()), "l:" + Other().Tag()),
            (T("bot.menu.back"), "m:set")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    public static Screen Chats(AppSettings s, long viewer, bool writable)
    {
        var rows = new List<List<TgInlineKeyboardButton>>();
        var text = new StringBuilder(T("bot.set.chat.title"));

        if (s.AuthorizedChatIds.Count == 0)
        {
            text.Append('\n').Append(T("bot.set.chat.none"));
        }
        else
        {
            text.Append('\n').Append(Strings.Format("bot.set.chat.count", s.AuthorizedChatIds.Count));
            foreach (var id in s.AuthorizedChatIds)
            {
                var name = TextUtil.Clip(s.NameFor(id), 32);
                var caption = id == viewer ? Strings.Format("bot.set.chat.you", name) : name;
                // A chat id is at most 20 characters, so it rides in the payload and
                // there is no list to go stale between rendering and the tap.
                rows.Add(Row(("👤 " + caption, "m:sch." + id.ToString(CultureInfo.InvariantCulture))));
            }
        }
        if (!writable)
            text.Append("\n\n").Append(T("bot.set.readonly"));

        rows.Add(Row((T("bot.menu.back"), "m:set")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    public static Screen Chat(long chatId, string name, bool isViewer, bool writable)
    {
        var text = new StringBuilder("👤 <b>").Append(TextUtil.Html(name)).Append("</b>\n<code>")
            .Append(chatId.ToString(CultureInfo.InvariantCulture)).Append("</code>");
        if (isViewer)
            text.Append('\n').Append(T("bot.set.chat.isyou"));

        var rows = new List<List<TgInlineKeyboardButton>>();
        if (writable)
        {
            var id = chatId.ToString(CultureInfo.InvariantCulture);
            rows.Add(Row((T("bot.set.chat.rename"), "i:rn." + id)));
            rows.Add(Row((T("bot.set.chat.revoke"), "c:cr." + id)));
        }
        else
        {
            text.Append("\n\n").Append(T("bot.set.readonly"));
        }
        rows.Add(Row((T("bot.menu.back"), "m:scht")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    public static Screen WindowsSettings(bool available) => new(
        available
            ? T("bot.set.win.title")
            : T("bot.set.win.title") + "\n" + T("bot.set.win.unavailable"),
        available
            ? Keyboard(
                Row((T("bot.set.win.plan"), "m:spln"), (T("bot.set.win.brightness"), "m:sbri")),
                Row((T("bot.set.win.wifi"), "m:swif"), (T("bot.set.win.bluetooth"), "m:sblu")),
                Row((T("bot.menu.back"), "m:set")))
            : Keyboard(Row((T("bot.menu.back"), "m:set"))));

    public static Screen PowerPlans(IReadOnlyList<PowerPlan> plans, bool writable)
    {
        var rows = new List<List<TgInlineKeyboardButton>>();
        var text = new StringBuilder(T("bot.set.plan.title"));
        if (plans.Count == 0)
            text.Append('\n').Append(T("bot.set.plan.none"));
        if (!writable)
            text.Append("\n\n").Append(T("bot.set.readonly"));

        foreach (var plan in plans)
        {
            // Composed here rather than as a catalogue row: a mark and a name that
            // Windows already supplies has nothing in it to translate.
            var caption = (plan.IsActive ? "✅ " : "▫️ ") + TextUtil.Clip(plan.Name, 30);
            if (writable && !plan.IsActive)
                rows.Add(Row((caption, "s:pln." + plan.Id)));
            else
                text.Append('\n').Append(caption);
        }

        rows.Add(Row((T("bot.menu.back"), "m:swin")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    public static Screen Brightness(BrightnessState state, bool writable)
    {
        var text = new StringBuilder(T("bot.set.bri.title"));
        var rows = new List<List<TgInlineKeyboardButton>>();

        if (!state.Supported)
        {
            text.Append('\n').Append(T("bot.set.bri.unsupported"));
        }
        else
        {
            if (state.Percent is { } now)
                text.Append('\n').Append(Strings.Format("bot.set.bri.now", now));
            if (writable)
            {
                rows.Add(new[] { 0, 25, 50 }
                    .Select(p => new TgInlineKeyboardButton(Strings.Format("bot.set.bri.level", p), $"s:bri.{p}"))
                    .ToList());
                rows.Add(new[] { 75, 100 }
                    .Select(p => new TgInlineKeyboardButton(Strings.Format("bot.set.bri.level", p), $"s:bri.{p}"))
                    .ToList());
                rows.Add(Row((T("bot.set.bri.custom"), "i:bri")));
            }
            else
            {
                text.Append("\n\n").Append(T("bot.set.readonly"));
            }
        }

        rows.Add(Row((T("bot.menu.back"), "m:swin")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    public static Screen Wifi(WifiState state, IReadOnlyList<string> profiles, bool writable)
    {
        var text = new StringBuilder(T("bot.set.wifi.title"));
        var rows = new List<List<TgInlineKeyboardButton>>();

        if (!state.AdapterPresent)
        {
            text.Append('\n').Append(T("bot.set.wifi.noadapter"));
        }
        else
        {
            text.Append('\n').Append(state.Connected
                ? state.ConnectedProfile is { Length: > 0 } ssid
                    ? Strings.Format("bot.set.wifi.connected", TextUtil.Html(ssid))
                    : T("bot.set.wifi.connectedunknown")
                : T("bot.set.wifi.disconnected"));

            if (writable)
            {
                if (state.Connected)
                    rows.Add(Row((T("bot.set.wifi.disconnect"), "c:wd")));

                if (profiles.Count == 0)
                {
                    text.Append('\n').Append(T("bot.set.wifi.noprofiles"));
                }
                else
                {
                    text.Append('\n').Append(T("bot.set.wifi.profiles"));
                    // An SSID is up to 32 characters of arbitrary Unicode — 128 bytes
                    // of UTF-8 — so it cannot ride in a 64-byte payload. The index
                    // into the list this screen just cached goes instead.
                    for (var i = 0; i < profiles.Count; i++)
                        rows.Add(Row(("📶 " + TextUtil.Clip(profiles[i], 30), $"s:wfc.{i}")));
                }
                rows.Add(Row((T("bot.set.wifi.refresh"), "m:swif")));
            }
            else
            {
                text.Append("\n\n").Append(T("bot.set.readonly"));
            }
        }

        rows.Add(Row((T("bot.menu.back"), "m:swin")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    public static Screen Bluetooth(RadioPower state, bool writable)
    {
        var text = new StringBuilder(T("bot.set.bt.title"));
        var rows = new List<List<TgInlineKeyboardButton>>();

        text.Append('\n').Append(state switch
        {
            RadioPower.On => T("bot.set.bt.on"),
            RadioPower.Off => T("bot.set.bt.off"),
            _ => T("bot.set.bt.none"),
        });

        if (state == RadioPower.Unavailable)
        {
            // nothing to offer
        }
        else if (writable)
        {
            rows.Add(state == RadioPower.On
                ? Row((T("bot.set.bt.turnoff"), "s:bt.0"))
                : Row((T("bot.set.bt.turnon"), "s:bt.1")));
        }
        else
        {
            text.Append("\n\n").Append(T("bot.set.readonly"));
        }

        rows.Add(Row((T("bot.menu.back"), "m:swin")));
        return new Screen(text.ToString(), Keyboard(rows));
    }

    /// <summary>
    /// A screen that only explains why something could not be read. It exists so a
    /// probe that throws still leaves the user somewhere with a way out, rather than
    /// on a panel that failed to render.
    /// </summary>
    public static Screen Unavailable(string message, string back) => new(
        "⚠️ " + TextUtil.Html(message),
        Keyboard(Row((T("bot.menu.back"), back))));

    /// <summary>
    /// One on/off row. When the section is writable it becomes a button whose payload
    /// carries the state it will set — the opposite of the current one — and when it
    /// is not, the same caption is appended to the message text instead.
    ///
    /// The payload is absolute rather than "flip" on purpose: a panel can sit in a
    /// chat for hours, and a tap on a stale one should land on the state its caption
    /// promised rather than toggling from whatever the value has since become.
    /// </summary>
    private static void Toggle(List<List<TgInlineKeyboardButton>> rows, StringBuilder text,
                               string labelKey, bool on, string action, bool writable)
    {
        var caption = (on ? "✅ " : "⬜ ") + T(labelKey);
        if (writable)
            rows.Add(Row((caption, $"{action}.{(on ? 0 : 1)}")));
        else
            text.Append('\n').Append(caption);
    }

    /// <summary>Shown while waiting for the user to send a value.</summary>
    public static TgInlineKeyboardMarkup CancelPrompt() =>
        Keyboard(Row((T("bot.prompt.cancel"), "x:cancel")));

    // ---- helpers ----

    private static string T(string key) => Strings.Get(key);

    private static TgInlineKeyboardMarkup Keyboard(params List<TgInlineKeyboardButton>[] rows)
        => new() { InlineKeyboard = rows.ToList() };

    private static TgInlineKeyboardMarkup Keyboard(List<List<TgInlineKeyboardButton>> rows)
        => new() { InlineKeyboard = rows };

    private static List<TgInlineKeyboardButton> Row(params (string text, string data)[] buttons)
        => buttons.Select(b => new TgInlineKeyboardButton(b.text, b.data)).ToList();
}
