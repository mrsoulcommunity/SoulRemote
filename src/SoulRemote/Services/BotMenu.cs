using SoulRemote.Models;

namespace SoulRemote.Services;

/// <summary>
/// Every screen the Telegram bot can show, and the keyboards that reach them.
///
/// The bot is driven by buttons, not typed commands: a tap edits the current
/// message into the next screen, so one message stays the control panel instead
/// of the chat filling with replies. Callback payloads are kept short because
/// Telegram caps callback_data at 64 bytes.
/// </summary>
public static class BotMenu
{
    // ---- persistent shortcut bar under the composer ----
    public const string BarMenu = "🎛 Menu";
    public const string BarShot = "📸 Screenshot";
    public const string BarLock = "🔒 Lock";
    public const string BarPower = "⚡ Power";

    public static TgReplyKeyboardMarkup ShortcutBar() => new()
    {
        ResizeKeyboard = true,
        IsPersistent = true,
        Placeholder = "Tap a control",
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
    public static Screen Home(string machine, string status) => new(
        $"🎛 <b>{TextUtil.Html(machine)}</b>\n{status}",
        Keyboard(
            Row(("📸 Capture", "m:cap"), ("⚡ Power", "m:pwr")),
            Row(("🔊 Audio & media", "m:aud"), ("⌨️ Input", "m:inp")),
            Row(("📊 System", "m:sys"), ("📋 Processes", "m:prc")),
            Row(("🔄 Refresh", "m:home"))));

    public static Screen Capture(int screenCount)
    {
        var rows = new List<List<TgInlineKeyboardButton>>
        {
            Row(("🖥 Whole desktop", "a:ss")),
        };
        if (screenCount > 1)
        {
            var perScreen = new List<TgInlineKeyboardButton>();
            for (var i = 0; i < Math.Min(screenCount, 4); i++)
                perScreen.Add(new TgInlineKeyboardButton($"Monitor {i + 1}", $"a:ss{i}"));
            rows.Add(perScreen);
        }
        rows.Add(Row(("⬅ Menu", "m:home")));
        return new Screen(
            screenCount > 1
                ? $"📸 <b>Capture</b>\n{screenCount} displays detected."
                : "📸 <b>Capture</b>",
            new TgInlineKeyboardMarkup { InlineKeyboard = rows });
    }

    public static Screen Power() => new(
        "⚡ <b>Power</b>\nThe last two ask you to confirm.",
        Keyboard(
            Row(("🔒 Lock", "a:lock"), ("🌙 Sleep", "a:sleep")),
            Row(("🌑 Display off", "a:mon"), ("💤 Hibernate", "c:hb")),
            Row(("🚪 Sign out", "c:lo"), ("🔄 Restart", "c:rs")),
            Row(("⏻ Shut down", "c:sd")),
            Row(("✋ Cancel a pending shutdown", "a:abort")),
            Row(("⬅ Menu", "m:home"))));

    public static Screen Audio() => new(
        "🔊 <b>Audio &amp; media</b>",
        Keyboard(
            Row(("🔉 Down", "a:vdn"), ("🔇 Mute", "a:mute"), ("🔊 Up", "a:vup")),
            Row(("⏮ Prev", "a:prev"), ("⏯ Play", "a:play"), ("⏭ Next", "a:next")),
            Row(("🎚 Set level…", "i:vol")),
            Row(("⬅ Menu", "m:home"))));

    public static Screen Input() => new(
        "⌨️ <b>Input &amp; clipboard</b>\nThese act on the signed-in desktop session.",
        Keyboard(
            Row(("📄 Read clipboard", "a:clip")),
            Row(("✏️ Set clipboard…", "i:clip"), ("⌨️ Type text…", "i:type")),
            Row(("🔗 Open link…", "i:open"), ("🗣 Speak…", "i:say")),
            Row(("⬅ Menu", "m:home"))));

    public static Screen System() => new(
        "📊 <b>System</b>",
        Keyboard(
            Row(("🖥 Overview", "a:sys"), ("💽 Disks", "a:disk")),
            Row(("🔋 Power status", "a:bat"), ("🌐 Network", "a:net")),
            Row(("⬅ Menu", "m:home"))));

    public static Screen Processes(bool shellAllowed)
    {
        var rows = new List<List<TgInlineKeyboardButton>>
        {
            Row(("📊 Top processes", "a:ps")),
            Row(("❌ End a process…", "i:kill")),
        };
        if (shellAllowed)
            rows.Add(Row(("⌨️ Run a command…", "i:cmd")));
        rows.Add(Row(("⬅ Menu", "m:home")));
        return new Screen(
            shellAllowed
                ? "⚙️ <b>Processes</b>"
                : "⚙️ <b>Processes</b>\nShell commands are switched off in the desktop app.",
            new TgInlineKeyboardMarkup { InlineKeyboard = rows });
    }

    public static Screen Confirm(string action, string question) => new(
        $"⚠️ <b>{TextUtil.Html(question)}</b>\nThis cannot be undone from here.",
        Keyboard(
            Row(("✅ Yes, do it", $"y:{action}"), ("❌ Cancel", "m:pwr"))));

    /// <summary>Shown while waiting for the user to send a value.</summary>
    public static TgInlineKeyboardMarkup CancelPrompt() =>
        Keyboard(Row(("✖ Cancel", "x:cancel")));

    // ---- helpers ----

    private static TgInlineKeyboardMarkup Keyboard(params List<TgInlineKeyboardButton>[] rows)
        => new() { InlineKeyboard = rows.ToList() };

    private static List<TgInlineKeyboardButton> Row(params (string text, string data)[] buttons)
        => buttons.Select(b => new TgInlineKeyboardButton(b.text, b.data)).ToList();
}
