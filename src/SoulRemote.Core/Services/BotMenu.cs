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

    public static Screen Input() => new(
        T("bot.input.title") + "\n" + T("bot.input.subtitle"),
        Keyboard(
            Row((T("bot.input.readclip"), "a:clip")),
            Row((T("bot.input.setclip"), "i:clip"), (T("bot.input.type"), "i:type")),
            Row((T("bot.input.open"), "i:open"), (T("bot.input.speak"), "i:say")),
            Row((T("bot.menu.back"), "m:home"))));

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

    public static Screen Confirm(string action, string question) => new(
        $"⚠️ <b>{TextUtil.Html(question)}</b>\n{T("bot.confirm.warning")}",
        Keyboard(
            Row((T("bot.confirm.yes"), $"y:{action}"), (T("bot.confirm.no"), "m:pwr"))));

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
