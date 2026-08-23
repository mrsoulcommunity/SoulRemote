namespace SoulRemote.Localization;

/// <summary>
/// The whole string catalogue, in one table, with both languages on the same row.
///
/// Keeping English and Persian side by side is deliberate: a two-dictionary design
/// lets a key exist in one language and not the other, and the missing half only
/// shows up in front of a user. Here a translator cannot add a key without
/// answering for both, and <c>StringsTests</c> enforces that neither half is blank
/// and that both use the same <c>{0}</c> placeholders.
/// </summary>
public static class Strings
{
    private static AppLanguage _current = AppLanguage.English;

    /// <summary>The language everything renders in until it is changed.</summary>
    public static AppLanguage Current
    {
        get => _current;
        private set => _current = value;
    }

    /// <summary>Raised after <see cref="Use"/> actually changes the language.</summary>
    public static event Action? LanguageChanged;

    public static bool IsRightToLeft => Current.IsRightToLeft();

    public static void Use(AppLanguage language)
    {
        if (_current == language)
            return;
        _current = language;
        LanguageChanged?.Invoke();
    }

    /// <summary>Every key in the catalogue. Exposed so tests can sweep the whole table.</summary>
    public static IReadOnlyCollection<string> Keys => Table.Keys;

    /// <summary>Looks a key up in the current language.</summary>
    public static string Get(string key) => Get(Current, key);

    /// <summary>
    /// Looks a key up in a specific language. An unknown key returns the key itself
    /// rather than throwing: a missing string should look wrong on screen, not take
    /// the bot's polling loop down.
    /// </summary>
    public static string Get(AppLanguage language, string key)
    {
        if (!Table.TryGetValue(key, out var row))
            return key;
        var text = language == AppLanguage.Persian ? row.Fa : row.En;
        return string.IsNullOrEmpty(text) ? row.En : text;
    }

    /// <summary>Looks a key up and fills its placeholders.</summary>
    public static string Format(string key, params object?[] args) => Format(Current, key, args);

    public static string Format(AppLanguage language, string key, params object?[] args)
    {
        var template = Get(language, key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // A malformed template must never break a reply; show the raw text instead.
            return template;
        }
    }

    /// <summary>True when the catalogue knows this key. Used by the tests and the tooling.</summary>
    public static bool Has(string key) => Table.ContainsKey(key);

    internal static (string En, string Fa) Row(string key) => Table[key];

    private static readonly Dictionary<string, (string En, string Fa)> Table = new(StringComparer.Ordinal)
    {
        // ================= Telegram bot: home =================
        ["bot.home.hint"] = ("Pick a control.", "یک کنترل را انتخاب کنید."),
        ["bot.home.up"] = ("up {0}", "{0} روشن"),
        ["bot.home.muted"] = ("muted", "بی‌صدا"),
        ["bot.home.volume"] = ("{0}%", "{0}٪"),
        ["bot.home.paired"] = ("{0} paired", "{0} چت متصل"),
        ["bot.menu.capture"] = ("📸 Capture", "📸 تصویربرداری"),
        ["bot.menu.power"] = ("⚡ Power", "⚡ روشن و خاموش"),
        ["bot.menu.audio"] = ("🔊 Audio & media", "🔊 صدا و رسانه"),
        ["bot.menu.input"] = ("⌨️ Input", "⌨️ ورودی"),
        ["bot.menu.system"] = ("📊 System", "📊 سیستم"),
        ["bot.menu.processes"] = ("📋 Processes", "📋 پروسه‌ها"),
        ["bot.menu.files"] = ("📁 Files", "📁 فایل‌ها"),
        ["bot.menu.refresh"] = ("🔄 Refresh", "🔄 تازه‌سازی"),
        ["bot.menu.back"] = ("⬅ Menu", "⬅ منو"),
        ["bot.menu.language"] = ("🌐 {0}", "🌐 {0}"),

        // ================= Telegram bot: capture =================
        ["bot.capture.title"] = ("📸 <b>Capture</b>", "📸 <b>تصویربرداری</b>"),
        ["bot.capture.displays"] = ("{0} displays detected.", "{0} نمایشگر شناسایی شد."),
        ["bot.capture.desktop"] = ("🖥 Whole desktop", "🖥 کل دسکتاپ"),
        ["bot.capture.monitor"] = ("Monitor {0}", "نمایشگر {0}"),
        ["bot.capture.working"] = ("Capturing…", "در حال گرفتن تصویر…"),
        ["bot.capture.caption.desktop"] = ("🖼 Desktop — {0}", "🖼 دسکتاپ — {0}"),
        ["bot.capture.caption.monitor"] = ("🖼 Monitor {0} — {1}", "🖼 نمایشگر {0} — {1}"),
        ["bot.capture.failed"] = ("❌ Screenshot failed: {0}", "❌ گرفتن تصویر ناموفق بود: {0}"),

        // ================= Telegram bot: power =================
        ["bot.power.title"] = ("⚡ <b>Power</b>", "⚡ <b>روشن و خاموش</b>"),
        ["bot.power.subtitle"] = ("The last two ask you to confirm.", "دو مورد آخر تأیید می‌خواهند."),
        ["bot.power.lock"] = ("🔒 Lock", "🔒 قفل"),
        ["bot.power.sleep"] = ("🌙 Sleep", "🌙 خواب"),
        ["bot.power.display"] = ("🌑 Display off", "🌑 خاموشی نمایشگر"),
        ["bot.power.hibernate"] = ("💤 Hibernate", "💤 خواب زمستانی"),
        ["bot.power.signout"] = ("🚪 Sign out", "🚪 خروج از حساب"),
        ["bot.power.restart"] = ("🔄 Restart", "🔄 راه‌اندازی مجدد"),
        ["bot.power.shutdown"] = ("⏻ Shut down", "⏻ خاموش کردن"),
        ["bot.power.abort"] = ("✋ Cancel a pending shutdown", "✋ لغو خاموشی در انتظار"),

        // ================= Telegram bot: confirmation =================
        ["bot.confirm.warning"] = ("This cannot be undone from here.", "این کار از اینجا برگشت‌پذیر نیست."),
        ["bot.confirm.yes"] = ("✅ Yes, do it", "✅ بله، انجام بده"),
        ["bot.confirm.no"] = ("❌ Cancel", "❌ انصراف"),
        ["bot.confirm.shutdown.toast"] = ("Confirm shut down", "تأیید خاموش کردن"),
        ["bot.confirm.shutdown.question"] = ("Shut down this PC?", "این رایانه خاموش شود؟"),
        ["bot.confirm.restart.toast"] = ("Confirm restart", "تأیید راه‌اندازی مجدد"),
        ["bot.confirm.restart.question"] = ("Restart this PC?", "این رایانه دوباره راه‌اندازی شود؟"),
        ["bot.confirm.signout.toast"] = ("Confirm sign out", "تأیید خروج از حساب"),
        ["bot.confirm.signout.question"] = ("Sign out of this PC?", "از حساب این رایانه خارج شوید؟"),
        ["bot.confirm.hibernate.toast"] = ("Confirm hibernate", "تأیید خواب زمستانی"),
        ["bot.confirm.hibernate.question"] = ("Hibernate this PC?", "این رایانه به خواب زمستانی برود؟"),
        ["bot.confirm.generic.toast"] = ("Confirm", "تأیید"),
        ["bot.confirm.generic.question"] = ("Are you sure?", "مطمئن هستید؟"),

        // ================= Telegram bot: audio =================
        ["bot.audio.title"] = ("🔊 <b>Audio &amp; media</b>", "🔊 <b>صدا و رسانه</b>"),
        ["bot.audio.down"] = ("🔉 Down", "🔉 کم"),
        ["bot.audio.mute"] = ("🔇 Mute", "🔇 بی‌صدا"),
        ["bot.audio.up"] = ("🔊 Up", "🔊 زیاد"),
        ["bot.audio.prev"] = ("⏮ Prev", "⏮ قبلی"),
        ["bot.audio.play"] = ("⏯ Play", "⏯ پخش"),
        ["bot.audio.next"] = ("⏭ Next", "⏭ بعدی"),
        ["bot.audio.setlevel"] = ("🎚 Set level…", "🎚 تنظیم میزان صدا…"),

        // ================= Telegram bot: input =================
        ["bot.input.title"] = ("⌨️ <b>Input &amp; clipboard</b>", "⌨️ <b>ورودی و کلیپ‌بورد</b>"),
        ["bot.input.subtitle"] = ("These act on the signed-in desktop session.", "این‌ها روی نشست دسکتاپِ واردشده اثر می‌گذارند."),
        ["bot.input.readclip"] = ("📄 Read clipboard", "📄 خواندن کلیپ‌بورد"),
        ["bot.input.setclip"] = ("✏️ Set clipboard…", "✏️ نوشتن در کلیپ‌بورد…"),
        ["bot.input.type"] = ("⌨️ Type text…", "⌨️ تایپ متن…"),
        ["bot.input.open"] = ("🔗 Open link…", "🔗 باز کردن لینک…"),
        ["bot.input.speak"] = ("🗣 Speak…", "🗣 خواندن با صدا…"),

        // ================= Telegram bot: system =================
        ["bot.system.title"] = ("📊 <b>System</b>", "📊 <b>سیستم</b>"),
        ["bot.system.overview"] = ("🖥 Overview", "🖥 نمای کلی"),
        ["bot.system.disks"] = ("💽 Disks", "💽 دیسک‌ها"),
        ["bot.system.battery"] = ("🔋 Power status", "🔋 وضعیت برق"),
        ["bot.system.network"] = ("🌐 Network", "🌐 شبکه"),

        // ================= Telegram bot: processes =================
        ["bot.proc.title"] = ("⚙️ <b>Processes</b>", "⚙️ <b>پروسه‌ها</b>"),
        ["bot.proc.shelloff"] = ("Shell commands are switched off in the desktop app.", "اجرای دستور در برنامهٔ دسکتاپ خاموش است."),
        ["bot.proc.top"] = ("📊 Top processes", "📊 پرمصرف‌ترین پروسه‌ها"),
        ["bot.proc.kill"] = ("❌ End a process…", "❌ بستن یک پروسه…"),
        ["bot.proc.run"] = ("⌨️ Run a command…", "⌨️ اجرای یک دستور…"),

        // ================= Telegram bot: files =================
        ["bot.files.title"] = ("📁 <b>Files</b>", "📁 <b>فایل‌ها</b>"),
        ["bot.files.off"] = ("File access is switched off in the desktop app.", "دسترسی به فایل در برنامهٔ دسکتاپ خاموش است."),
        ["bot.files.send"] = ("📤 Send me a file…", "📤 فایل را برایم بفرست…"),
        ["bot.files.browse"] = ("📂 Browse a folder…", "📂 مرور یک پوشه…"),
        ["bot.files.up"] = ("⬆ Up one level", "⬆ یک پوشه بالاتر"),
        ["bot.files.empty"] = ("This folder is empty.", "این پوشه خالی است."),
        ["bot.files.listing"] = ("📂 <b>{0}</b>", "📂 <b>{0}</b>"),
        ["bot.files.toobig"] = ("That file is {0} — Telegram only accepts up to {1} from a bot.", "حجم آن فایل {0} است — تلگرام از یک بات تا {1} می‌پذیرد."),
        ["bot.files.notfound"] = ("No such file or folder on this PC.", "چنین فایل یا پوشه‌ای روی این رایانه نیست."),
        ["bot.files.sent"] = ("📎 {0}", "📎 {0}"),
        ["bot.files.saved"] = ("✅ Saved to <code>{0}</code>", "✅ در <code>{0}</code> ذخیره شد."),
        ["bot.files.denied"] = ("Windows would not let me read that.", "ویندوز اجازهٔ خواندن آن را نداد."),
        ["bot.files.truncated"] = ("Showing the first {0} items.", "{0} مورد نخست نمایش داده می‌شود."),
        ["bot.files.folder"] = ("📁 {0}", "📁 {0}"),
        ["bot.files.file"] = ("📄 {0}", "📄 {0}"),
        ["bot.files.receiving"] = ("📥 Receiving…", "📥 در حال دریافت…"),
        ["bot.files.stale"] = ("That listing has moved on — open the folder again.", "آن فهرست تازه نیست — پوشه را دوباره باز کنید."),
        ["bot.files.roots"] = ("Pick a drive, or send a full path.", "یک درایو انتخاب کنید یا مسیر کامل بفرستید."),

        // ================= Telegram bot: shortcut bar =================
        ["bot.bar.menu"] = ("🎛 Menu", "🎛 منو"),
        ["bot.bar.shot"] = ("📸 Screenshot", "📸 اسکرین‌شات"),
        ["bot.bar.lock"] = ("🔒 Lock", "🔒 قفل"),
        ["bot.bar.power"] = ("⚡ Power", "⚡ برق"),
        ["bot.bar.placeholder"] = ("Tap a control", "یک کنترل را بزنید"),

        // ================= Telegram bot: prompts =================
        ["bot.prompt.cancel"] = ("✖ Cancel", "✖ انصراف"),
        ["bot.prompt.cancelled"] = ("Cancelled", "لغو شد"),
        ["bot.prompt.volume"] = ("Send a level from <b>0</b> to <b>100</b>.", "عددی بین <b>۰</b> تا <b>۱۰۰</b> بفرستید."),
        ["bot.prompt.kill"] = ("Send the process <b>name</b> or <b>PID</b> to end.", "<b>نام</b> یا <b>شناسهٔ</b> پروسه‌ای که باید بسته شود را بفرستید."),
        ["bot.prompt.clip"] = ("Send the text to put on the clipboard.", "متنی که باید در کلیپ‌بورد بنشیند را بفرستید."),
        ["bot.prompt.type"] = ("Send the text to type into the focused window.", "متنی که باید در پنجرهٔ فعال تایپ شود را بفرستید."),
        ["bot.prompt.speak"] = ("Send the text to speak aloud.", "متنی که باید با صدا خوانده شود را بفرستید."),
        ["bot.prompt.open"] = ("Send a web link to open. Files and folders need file access switched on.", "یک لینک وب بفرستید. باز کردن فایل و پوشه به روشن بودن دسترسی فایل نیاز دارد."),
        ["bot.prompt.shell"] = ("Send the command to run.", "دستوری که باید اجرا شود را بفرستید."),
        ["bot.prompt.path"] = ("Send the full path of the file or folder.", "مسیر کامل فایل یا پوشه را بفرستید."),
        ["bot.prompt.generic"] = ("Send a value.", "یک مقدار بفرستید."),
        ["bot.placeholder.volume"] = ("0-100", "۰ تا ۱۰۰"),
        ["bot.placeholder.kill"] = ("chrome or 1234", "chrome یا ۱۲۳۴"),
        ["bot.placeholder.open"] = ("https://…", "https://…"),
        ["bot.placeholder.shell"] = ("ipconfig /all", "ipconfig /all"),
        ["bot.placeholder.path"] = (@"C:\Users\…", @"C:\Users\…"),
        ["bot.placeholder.generic"] = ("Type here", "اینجا بنویسید"),
        ["bot.prompt.notanumber"] = ("That is not a number between 0 and 100.", "این عددی بین ۰ تا ۱۰۰ نیست."),

        // ================= Telegram bot: replies =================
        ["bot.welcome"] = ("👋 Connected to <b>{0}</b>.\nUse the buttons below — no commands to remember.",
                           "👋 به <b>{0}</b> وصل شدید.\nاز دکمه‌های پایین استفاده کنید — لازم نیست دستوری به خاطر بسپارید."),
        ["bot.online"] = ("🟢 <b>Soul Remote</b> online on <b>{0}</b>.\nUse the buttons below.",
                          "🟢 <b>سول ریموت</b> روی <b>{0}</b> آنلاین شد.\nاز دکمه‌های پایین استفاده کنید."),
        ["bot.test"] = ("🛰 Test from <b>{0}</b> — the relay is working.", "🛰 پیام آزمایشی از <b>{0}</b> — رله کار می‌کند."),
        ["bot.notauthorized"] = ("Not authorized", "اجازه ندارید"),
        ["bot.unpaired"] = ("👋 <b>Soul Remote</b>\n\nThis chat is not linked yet.\nOpen the Soul Remote app, then send:\n<code>/pair YOURCODE</code>",
                            "👋 <b>سول ریموت</b>\n\nاین چت هنوز متصل نشده است.\nبرنامهٔ سول ریموت را باز کنید و بفرستید:\n<code>/pair کد‌شما</code>"),
        ["bot.pair.closed"] = ("⛔ Pairing is closed. Generate a fresh code in the Soul Remote app and try again.",
                               "⛔ اتصال بسته است. در برنامهٔ سول ریموت کد تازه بسازید و دوباره تلاش کنید."),
        ["bot.pair.wrong"] = ("❌ That code is not right.", "❌ این کد درست نیست."),
        ["bot.pair.slowdown"] = ("⏳ Too many tries. Wait a moment and try again.", "⏳ تلاش‌های زیاد. کمی صبر کنید و دوباره تلاش کنید."),
        ["bot.pair.privateonly"] = ("🔒 Pair from a private chat with the bot, not from a group — pairing a group would give every member control of this PC.",
                                    "🔒 اتصال را در چت خصوصی با بات انجام دهید، نه در گروه — اتصال یک گروه کنترل این رایانه را به همهٔ اعضا می‌دهد."),
        ["bot.pair.savefailed"] = ("⚠️ The pairing could not be saved on the PC. Check the Soul Remote window and try again.",
                                   "⚠️ اتصال روی رایانه ذخیره نشد. پنجرهٔ سول ریموت را ببینید و دوباره تلاش کنید."),
        ["bot.prompt.cancel.hint"] = ("Send the value above, or:", "مقدار بالا را بفرستید، یا:"),
        ["bot.chatid"] = ("Your chat ID: <code>{0}</code>", "شناسهٔ چت شما: <code>{0}</code>"),
        ["bot.pong"] = ("🏓 pong", "🏓 پونگ"),
        ["bot.nothing"] = ("Nothing to do.", "کاری برای انجام نیست."),
        ["bot.shell.off"] = ("🔒 Shell commands are switched off in the desktop app.", "🔒 اجرای دستور در برنامهٔ دسکتاپ خاموش است."),
        ["bot.shell.offtoast"] = ("Shell commands are switched off in the desktop app.", "اجرای دستور در برنامهٔ دسکتاپ خاموش است."),
        ["bot.open.weblinksonly"] = ("🔒 Only web links are allowed. To open files and folders on this PC, turn on file access in the Soul Remote app.",
                                     "🔒 فقط لینک وب مجاز است. برای باز کردن فایل و پوشه روی این رایانه، دسترسی فایل را در برنامهٔ سول ریموت روشن کنید."),
        ["bot.file.off"] = ("🔒 File access is switched off in the desktop app.", "🔒 دسترسی به فایل در برنامهٔ دسکتاپ خاموش است."),
        ["bot.clipboard.empty"] = ("Clipboard holds no text.", "کلیپ‌بورد متنی ندارد."),
        ["bot.clipboard.caption"] = ("Clipboard", "کلیپ‌بورد"),
        ["bot.shell.caption"] = ("Command output", "خروجی دستور"),
        ["bot.ratelimited"] = ("⏳ Slow down a moment — too many commands at once.", "⏳ کمی آرام‌تر — دستورهای پشت‌سرهم زیاد است."),
        ["bot.language.changed"] = ("🌐 Language set to English.", "🌐 زبان روی فارسی تنظیم شد."),
        ["bot.ok"] = ("✅ {0}", "✅ {0}"),
        ["bot.err"] = ("❌ {0}", "❌ {0}"),

        // ================= System reports (rendered on this PC) =================
        ["sys.info.title"] = ("<b>🖥 System Information</b>", "<b>🖥 اطلاعات سیستم</b>"),
        ["sys.info.machine"] = ("Machine", "رایانه"),
        ["sys.info.user"] = ("User", "کاربر"),
        ["sys.info.os"] = ("OS", "سیستم‌عامل"),
        ["sys.info.arch"] = ("Architecture", "معماری"),
        ["sys.info.cpu"] = ("CPU", "پردازنده"),
        ["sys.info.cores"] = ("{0} logical cores", "{0} هستهٔ منطقی"),
        ["sys.info.load"] = ("CPU load", "بار پردازنده"),
        ["sys.info.ram"] = ("RAM", "حافظه"),
        ["sys.info.ramused"] = ("{0} / {1} used ({2}%)", "{0} از {1} در استفاده ({2}٪)"),
        ["sys.info.uptime"] = ("Uptime", "مدت روشن بودن"),
        ["sys.info.localtime"] = ("Local time", "زمان محلی"),
        ["sys.disks.title"] = ("<b>💽 Disks</b>", "<b>💽 دیسک‌ها</b>"),
        ["sys.disks.free"] = ("free {0}", "{0} آزاد"),
        ["sys.power.title"] = ("<b>🔋 Power</b>", "<b>🔋 برق</b>"),
        ["sys.power.line"] = ("Line", "برق شهر"),
        ["sys.power.battery"] = ("Battery", "باتری"),
        ["sys.power.nobattery"] = ("none (desktop).", "ندارد (رایانهٔ رومیزی)."),
        ["sys.power.remaining"] = ("Remaining", "باقی‌مانده"),
        ["sys.power.unavailable"] = ("Power status unavailable: {0}", "وضعیت برق در دسترس نیست: {0}"),
        ["sys.proc.title"] = ("<b>📋 Top {0} processes by memory</b>", "<b>📋 {0} پروسهٔ پرمصرف از نظر حافظه</b>"),
        ["sys.net.title"] = ("<b>🌐 Network</b>", "<b>🌐 شبکه</b>"),
        ["sys.net.host"] = ("Host", "نام میزبان"),
        ["sys.net.local"] = ("Local IPs", "آی‌پی‌های محلی"),
        ["sys.net.public"] = ("Public IP", "آی‌پی عمومی"),
        ["sys.net.none"] = ("none", "هیچ"),
        ["sys.net.unavailable"] = ("unavailable", "در دسترس نیست"),
        ["sys.net.error"] = ("error ({0})", "خطا ({0})"),

        // ================= Local actions (results reported back to Telegram) =================
        ["act.locked"] = ("Workstation locked.", "رایانه قفل شد."),
        ["act.lockfailed"] = ("Windows refused to lock the workstation.", "ویندوز قفل کردن رایانه را نپذیرفت."),
        ["act.sleeping"] = ("System going to sleep.", "سیستم به خواب می‌رود."),
        ["act.sleepfailed"] = ("Windows refused to put this PC to sleep.", "ویندوز خواباندن این رایانه را نپذیرفت."),
        ["act.hibernating"] = ("System hibernating.", "سیستم به خواب زمستانی می‌رود."),
        ["act.hibernatefailed"] = ("Hibernate failed — it may be disabled on this machine.", "خواب زمستانی ناموفق بود — شاید روی این رایانه غیرفعال باشد."),
        ["act.displayoff"] = ("Display turned off.", "نمایشگر خاموش شد."),
        ["act.shutdown"] = ("System will shut down in {0} seconds. Send /cancel to abort.", "سیستم تا {0} ثانیهٔ دیگر خاموش می‌شود. برای لغو /cancel بفرستید."),
        ["act.restart"] = ("System will restart in {0} seconds. Send /cancel to abort.", "سیستم تا {0} ثانیهٔ دیگر دوباره راه‌اندازی می‌شود. برای لغو /cancel بفرستید."),
        ["act.logoff"] = ("Logging off the current user…", "در حال خروج کاربر فعلی…"),
        ["act.aborted"] = ("Pending shutdown/restart cancelled.", "خاموشی یا راه‌اندازی مجدد در انتظار لغو شد."),
        ["act.volumeset"] = ("Volume set to {0}%.", "صدا روی {0}٪ تنظیم شد."),
        ["act.volumefailed"] = ("Unable to set the volume on this PC.", "تنظیم صدای این رایانه ممکن نشد."),
        ["act.volumeup"] = ("Volume up. {0}", "صدا زیاد شد. {0}"),
        ["act.volumedown"] = ("Volume down. {0}", "صدا کم شد. {0}"),
        ["act.volumenow"] = ("Now ~{0}%.", "اکنون حدود {0}٪."),
        ["act.muted"] = ("Muted.", "بی‌صدا شد."),
        ["act.unmuted"] = ("Unmuted.", "صدا برگشت."),
        ["act.mutetoggled"] = ("Toggled mute.", "بی‌صدا جابه‌جا شد."),
        ["act.playpause"] = ("Media play/pause.", "پخش یا مکث رسانه."),
        ["act.nexttrack"] = ("Next track.", "قطعهٔ بعدی."),
        ["act.prevtrack"] = ("Previous track.", "قطعهٔ قبلی."),
        ["act.killedpid"] = ("Killed process {0} (PID {1}).", "پروسهٔ {0} (شناسهٔ {1}) بسته شد."),
        ["act.killedname"] = ("Killed {0} instance(s) of '{1}'.", "{0} نمونه از «{1}» بسته شد."),
        ["act.killnone"] = ("No running process named '{0}'.", "پروسهٔ در حال اجرایی با نام «{0}» نیست."),
        ["act.killneedsname"] = ("Give me a process name or PID.", "نام پروسه یا شناسهٔ آن را بدهید."),
        ["act.clipboardset"] = ("Clipboard updated.", "کلیپ‌بورد به‌روز شد."),
        ["act.opened"] = ("Opened {0}", "{0} باز شد"),
        ["act.openneedstarget"] = ("Give me a URL, file or folder to open.", "یک نشانی، فایل یا پوشه برای باز کردن بدهید."),
        ["act.typed"] = ("Typed {0} character(s) into the focused window.", "{0} نویسه در پنجرهٔ فعال تایپ شد."),
        ["act.typeneedstext"] = ("Give me some text to type.", "متنی برای تایپ بدهید."),
        ["act.spoken"] = ("Spoken on this PC.", "روی این رایانه خوانده شد."),
        ["act.speakneedstext"] = ("Give me something to say.", "متنی برای خواندن بدهید."),
        ["act.speakfailed"] = ("Speech failed.", "خواندن متن ناموفق بود."),
        ["act.speaktimeout"] = ("Speech stopped after {0} seconds.", "خواندن پس از {0} ثانیه متوقف شد."),
        ["act.shelltimeout"] = ("Command timed out after {0} seconds and was terminated.", "دستور پس از {0} ثانیه بی‌پاسخ ماند و بسته شد."),
        ["act.shellnooutput"] = ("(no output, exit code {0})", "(بدون خروجی، کد پایان {0})"),
        ["act.processexit"] = ("{0} exited with code {1}.", "{0} با کد {1} پایان یافت."),

        // ================= Desktop: shell =================
        ["ui.app.name"] = ("SOUL REMOTE", "سول ریموت"),
        ["ui.app.tagline"] = ("relay console", "کنسول رله"),
        ["ui.nav.dashboard"] = ("Dashboard", "داشبورد"),
        ["ui.nav.connect"] = ("Connect", "اتصال"),
        ["ui.nav.settings"] = ("Settings", "تنظیمات"),
        ["ui.nav.logs"] = ("Activity", "فعالیت"),
        ["ui.crumb.dashboard"] = ("DASHBOARD", "داشبورد"),
        ["ui.crumb.connect"] = ("CONNECT", "اتصال"),
        ["ui.crumb.settings"] = ("SETTINGS", "تنظیمات"),
        ["ui.crumb.logs"] = ("ACTIVITY", "فعالیت"),
        ["ui.quick.title"] = ("QUICK CONTROLS", "کنترل‌های سریع"),
        ["ui.quick.lock"] = ("Lock", "قفل"),
        ["ui.quick.sleep"] = ("Sleep", "خواب"),
        ["ui.quick.display"] = ("Display off", "خاموشی نمایشگر"),
        ["ui.chrome.minimise"] = ("Minimise", "کوچک کردن"),
        ["ui.chrome.maximise"] = ("Maximise", "بزرگ کردن"),
        ["ui.chrome.hide"] = ("Hide to tray", "بردن به سینی"),

        // ================= Desktop: status =================
        ["ui.status.online"] = ("Relay online", "رله آنلاین"),
        ["ui.status.connecting"] = ("Connecting", "در حال اتصال"),
        ["ui.status.fault"] = ("Link fault", "خطای پیوند"),
        ["ui.status.offline"] = ("Offline", "آفلاین"),

        // ================= Desktop: dashboard =================
        ["ui.dash.sub.listening"] = ("Listening for commands from your paired chats.", "در انتظار دستور از چت‌های متصل‌شده."),
        ["ui.dash.sub.warning"] = ("Listening, with a warning: {0}", "در حال شنیدن، با یک هشدار: {0}"),
        ["ui.dash.sub.connecting"] = ("Bringing the relay up through Cloudflare…", "در حال بالا آوردن رله از راه کلادفلر…"),
        ["ui.dash.sub.fault"] = ("The relay stopped unexpectedly.", "رله به‌طور ناگهانی متوقف شد."),
        ["ui.dash.sub.ready"] = ("Press Start relay to begin listening.", "برای شروع، «شروع رله» را بزنید."),
        ["ui.dash.sub.notready"] = ("Connect Cloudflare and Telegram to start.", "برای شروع، کلادفلر و تلگرام را وصل کنید."),
        ["ui.dash.start"] = ("Start relay", "شروع رله"),
        ["ui.dash.stop"] = ("Stop relay", "توقف رله"),
        ["ui.dash.test"] = ("Send test message", "ارسال پیام آزمایشی"),
        ["ui.dash.lockpc"] = ("Lock this PC", "قفل این رایانه"),
        ["ui.dash.stat.commands"] = ("COMMANDS RUN", "دستورهای اجراشده"),
        ["ui.dash.stat.chats"] = ("PAIRED CHATS", "چت‌های متصل"),
        ["ui.dash.stat.uptime"] = ("MACHINE UPTIME", "مدت روشن بودن"),
        ["ui.dash.since"] = ("since {0}", "از {0}"),
        ["ui.dash.nochats"] = ("No chats paired yet", "هنوز چتی متصل نشده"),
        ["ui.dash.notdeployed"] = ("not deployed", "مستقر نشده"),
        ["ui.dash.nobot"] = ("no bot", "بدون بات"),
        ["ui.dash.pairedlist"] = ("WHO CAN CONTROL THIS PC", "چه کسانی این رایانه را کنترل می‌کنند"),
        ["ui.relay.pc"] = ("THIS PC", "این رایانه"),
        ["ui.relay.edge"] = ("CLOUDFLARE EDGE", "لبهٔ کلادفلر"),
        ["ui.relay.telegram"] = ("TELEGRAM", "تلگرام"),
        ["ui.dash.pair.title"] = ("PAIR A CHAT", "اتصال یک چت"),
        ["ui.dash.pair.help"] = ("Open your bot in Telegram and send this command. The code works once, then a new one is issued.",
                                 "بات خود را در تلگرام باز کنید و این دستور را بفرستید. کد یک‌بار مصرف است و بعد کد تازه‌ای صادر می‌شود."),
        ["ui.dash.copy"] = ("Copy", "کپی"),
        ["ui.dash.newcode"] = ("New code", "کد تازه"),
        ["ui.dash.revokeall"] = ("Revoke all", "لغو همه"),
        ["ui.dash.remove"] = ("Remove", "حذف"),
        ["ui.dash.revoke.confirm"] = ("Revoke every paired Telegram chat? They will need a new pairing code to control this machine.",
                                      "همهٔ چت‌های متصل لغو شوند؟ برای کنترل این رایانه به کد اتصال تازه نیاز خواهند داشت."),
        ["ui.dash.remove.confirm"] = ("Remove chat {0}? It will need a new pairing code to control this machine.",
                                      "چت {0} حذف شود؟ برای کنترل این رایانه به کد اتصال تازه نیاز خواهد داشت."),
        ["ui.dash.test.nochats"] = ("Pair a Telegram chat first — send /pair with the code below.", "ابتدا یک چت تلگرام را متصل کنید — /pair را با کد زیر بفرستید."),
        ["ui.dash.test.failed"] = ("Test message failed: {0}", "پیام آزمایشی نرسید: {0}"),
        ["ui.dash.test.sent"] = ("Test message delivered.", "پیام آزمایشی رسید."),

        // ================= Desktop: connect =================
        ["ui.connect.title"] = ("Bring the relay up", "رله را بالا بیاورید"),
        ["ui.connect.intro"] = ("Telegram is unreachable on some networks, so Soul Remote routes every request through a small worker on Cloudflare's edge. Paste both tokens once and Connect does the rest: deploy the worker, publish its route, then sign the bot in through it.",
                                "تلگرام روی برخی شبکه‌ها در دسترس نیست، بنابراین سول ریموت هر درخواست را از یک ورکر کوچک روی لبهٔ کلادفلر عبور می‌دهد. هر دو توکن را یک‌بار جای‌گذاری کنید و «اتصال» بقیه را انجام می‌دهد: ورکر را مستقر می‌کند، مسیرش را منتشر می‌کند و بات را از همان مسیر وارد می‌کند."),
        ["ui.connect.cf"] = ("CLOUDFLARE", "کلادفلر"),
        ["ui.connect.cf.role"] = ("the bridge", "پل ارتباطی"),
        ["ui.connect.cf.field"] = ("API token", "توکن API"),
        ["ui.connect.cf.hint.new"] = ("Create one with the “Edit Cloudflare Workers” template.", "با قالب «Edit Cloudflare Workers» یکی بسازید."),
        ["ui.connect.cf.hint.saved"] = ("A token is already saved. Leave this blank to keep it, or paste a new one to replace it.",
                                        "یک توکن ذخیره شده است. برای نگه داشتنش این را خالی بگذارید یا توکن تازه‌ای جای‌گذاری کنید."),
        ["ui.connect.cf.open"] = ("Open Cloudflare tokens", "باز کردن توکن‌های کلادفلر"),
        ["ui.connect.worker"] = ("Worker name", "نام ورکر"),
        ["ui.connect.tg"] = ("TELEGRAM", "تلگرام"),
        ["ui.connect.tg.role"] = ("the remote", "کنترل از راه دور"),
        ["ui.connect.tg.field"] = ("Bot token", "توکن بات"),
        ["ui.connect.tg.hint.new"] = ("Ask @BotFather for /newbot, then paste the HTTP API token.", "از @BotFather دستور /newbot را بگیرید و توکن HTTP API را جای‌گذاری کنید."),
        ["ui.connect.tg.hint.saved"] = ("A bot token is already saved. Leave this blank to keep it, or paste a new one to replace it.",
                                        "یک توکن بات ذخیره شده است. برای نگه داشتنش این را خالی بگذارید یا توکن تازه‌ای جای‌گذاری کنید."),
        ["ui.connect.tg.open"] = ("Open BotFather", "باز کردن BotFather"),
        ["ui.connect.go"] = ("Connect", "اتصال"),
        ["ui.connect.cancel"] = ("Cancel", "انصراف"),
        ["ui.connect.sequence"] = ("BRING-UP SEQUENCE", "مراحل راه‌اندازی"),
        ["ui.connect.endpoint"] = ("RELAY ENDPOINT", "نشانی رله"),
        ["ui.connect.working"] = ("Bringing the relay up…", "در حال بالا آوردن رله…"),
        ["ui.connect.done"] = ("Connected as @{0}. Open Telegram and send /pair with the code on the dashboard.",
                               "با @{0} متصل شد. تلگرام را باز کنید و /pair را با کد داشبورد بفرستید."),
        ["ui.connect.failed"] = ("Connection failed.", "اتصال ناموفق بود."),
        ["ui.connect.needcf"] = ("Paste your Cloudflare API token first.", "ابتدا توکن API کلادفلر را جای‌گذاری کنید."),
        ["ui.connect.needtg"] = ("Paste your Telegram bot token first.", "ابتدا توکن بات تلگرام را جای‌گذاری کنید."),
        ["ui.connect.inflight"] = ("A connection run is already in progress.", "یک اتصال هم‌اکنون در جریان است."),
        ["ui.connect.cancelled"] = ("Connection cancelled.", "اتصال لغو شد."),
        ["ui.connect.browserfailed"] = ("Could not open the browser: {0}", "مرورگر باز نشد: {0}"),

        // ================= Desktop: connect pipeline steps =================
        ["ui.step.verify"] = ("Verify Cloudflare token", "بررسی توکن کلادفلر"),
        ["ui.step.account"] = ("Resolve account", "یافتن حساب"),
        ["ui.step.subdomain"] = ("Find workers.dev subdomain", "یافتن زیردامنهٔ workers.dev"),
        ["ui.step.deploy"] = ("Deploy relay worker", "استقرار ورکر رله"),
        ["ui.step.route"] = ("Publish public route", "انتشار مسیر عمومی"),
        ["ui.step.probe"] = ("Reach the edge", "آزمودن لبه"),
        ["ui.step.bot"] = ("Authenticate Telegram bot", "احراز هویت بات تلگرام"),
        ["ui.step.listen"] = ("Start listening", "شروع شنیدن"),
        ["ui.step.tokenactive"] = ("Token is active", "توکن فعال است"),
        ["ui.step.routeunconfirmed"] = ("Route not confirmed — continuing", "مسیر تأیید نشد — ادامه می‌دهیم"),
        ["ui.step.edgeanswering"] = ("Edge is answering", "لبه پاسخ می‌دهد"),
        ["ui.step.propagating"] = ("Still propagating — continuing", "هنوز در حال انتشار — ادامه می‌دهیم"),
        ["ui.step.listening"] = ("Listening for commands", "در انتظار دستور"),
        ["ui.step.cancelled"] = ("Cancelled", "لغو شد"),

        // ================= Desktop: settings =================
        ["ui.settings.title"] = ("Settings", "تنظیمات"),
        ["ui.settings.autosave"] = ("Changes save as you make them.", "تغییرات همان لحظه ذخیره می‌شوند."),
        ["ui.settings.saved"] = ("Saved {0}", "ذخیره شد {0}"),
        ["ui.settings.language"] = ("LANGUAGE", "زبان"),
        ["ui.settings.language.hint"] = ("Applies to this window and to everything the bot says in Telegram.", "روی این پنجره و روی هر چیزی که بات در تلگرام می‌گوید اعمال می‌شود."),
        ["ui.settings.startup"] = ("STARTUP", "راه‌اندازی"),
        ["ui.settings.startwin"] = ("Start Soul Remote when I sign in", "اجرای سول ریموت هنگام ورود به ویندوز"),
        ["ui.settings.startwin.hint"] = ("Adds a per-user entry to Windows startup. No admin rights needed.", "یک ورودی کاربری به راه‌اندازی ویندوز اضافه می‌کند. نیازی به دسترسی مدیر نیست."),
        ["ui.settings.startmin"] = ("Start hidden in the tray", "شروع پنهان در سینی سیستم"),
        ["ui.settings.autostart"] = ("Start the relay automatically", "شروع خودکار رله"),
        ["ui.settings.autostart.hint"] = ("Begins listening as soon as the app launches, so the PC is reachable after a reboot.",
                                          "به‌محض اجرای برنامه شنیدن آغاز می‌شود تا رایانه پس از راه‌اندازی مجدد در دسترس باشد."),
        ["ui.settings.notify"] = ("Announce in Telegram when the relay comes online", "اعلام در تلگرام وقتی رله آنلاین می‌شود"),
        ["ui.settings.permissions"] = ("WHAT THE BOT MAY DO", "اختیارات بات"),
        ["ui.settings.shell"] = ("Allow shell commands", "اجازهٔ اجرای دستور"),
        ["ui.settings.shell.hint"] = ("Lets a paired chat run any command on this PC. Leave off unless you need it.",
                                      "به چت متصل اجازه می‌دهد هر دستوری را روی این رایانه اجرا کند. تا وقتی لازم نیست خاموش بماند."),
        ["ui.settings.files"] = ("Allow file access", "اجازهٔ دسترسی به فایل"),
        ["ui.settings.files.hint"] = ("Lets a paired chat browse folders, fetch files and open local paths.",
                                      "به چت متصل اجازه می‌دهد پوشه‌ها را مرور کند، فایل بگیرد و مسیرهای محلی را باز کند."),
        ["ui.settings.interface"] = ("INTERFACE", "رابط کاربری"),
        ["ui.settings.reducemotion"] = ("Reduce motion", "کاهش حرکت"),
        ["ui.settings.reducemotion.hint"] = ("Stops the relay line animating.", "انیمیشن خط رله را متوقف می‌کند."),
        ["ui.settings.advanced"] = ("ADVANCED", "پیشرفته"),
        ["ui.settings.polltimeout"] = ("Long-poll timeout (seconds)", "مهلت long-poll (ثانیه)"),
        ["ui.settings.polltimeout.hint"] = ("How long each getUpdates call waits. 25 suits most networks; lower it on a flaky link.",
                                            "هر فراخوانی getUpdates چقدر منتظر بماند. ۲۵ برای بیشتر شبکه‌ها مناسب است؛ روی پیوند ناپایدار کمترش کنید."),
        ["ui.settings.logretention"] = ("Keep activity logs for (days)", "نگهداری گزارش فعالیت (روز)"),
        ["ui.settings.logretention.hint"] = ("Older log files are deleted when the app starts.", "فایل‌های گزارش قدیمی‌تر هنگام اجرای برنامه حذف می‌شوند."),
        ["ui.settings.openlogs"] = ("Open log folder", "باز کردن پوشهٔ گزارش"),
        ["ui.settings.opensettings"] = ("Open settings folder", "باز کردن پوشهٔ تنظیمات"),
        ["ui.settings.permissions.hint"] = ("Power, screenshot, media and process controls are always available to paired chats. These two are off until you turn them on.",
                                            "کنترل برق، اسکرین‌شات، رسانه و پروسه‌ها همیشه در دسترس چت‌های متصل است. این دو تا وقتی روشنشان نکنید خاموش‌اند."),
        ["ui.settings.shell.label"] = ("Allow shell commands (/cmd)", "اجازهٔ اجرای دستور (/cmd)"),
        ["ui.settings.files.label"] = ("Allow browsing and fetching files", "اجازهٔ مرور و دریافت فایل"),
        ["ui.settings.onthispc"] = ("ON THIS PC", "روی این رایانه"),
        ["ui.settings.dpapi"] = ("Tokens in this file are encrypted with Windows DPAPI and can only be read by your account on this machine.",
                                 "توکن‌های این فایل با DPAPI ویندوز رمزنگاری شده‌اند و فقط با حساب شما روی همین رایانه خوانده می‌شوند."),
        ["ui.settings.downloads"] = ("Files received from Telegram", "فایل‌های دریافتی از تلگرام"),
        ["ui.settings.downloads.hint"] = ("Where a file sent to the bot is saved. Leave blank for Downloads\\Soul Remote.",
                                          "فایلی که به بات فرستاده می‌شود کجا ذخیره شود. برای Downloads\\Soul Remote خالی بگذارید."),
        ["ui.settings.english"] = ("English", "English"),
        ["ui.settings.persian"] = ("فارسی", "فارسی"),

        // ================= Desktop: activity log =================
        ["ui.logs.title"] = ("Activity", "فعالیت"),
        ["ui.logs.subtitle"] = ("Everything the relay has done since the app started.", "هر کاری که رله از زمان اجرای برنامه انجام داده است."),
        ["ui.logs.all"] = ("All", "همه"),
        ["ui.logs.problems"] = ("Problems", "مشکل‌ها"),
        ["ui.logs.errors"] = ("Errors", "خطاها"),
        ["ui.logs.clear"] = ("Clear", "پاک کردن"),
        ["ui.logs.empty"] = ("Nothing logged yet.", "هنوز چیزی ثبت نشده است."),

        // ================= Desktop: tray & dialogs =================
        ["ui.tray.open"] = ("Open", "باز کردن"),
        ["ui.tray.startbot"] = ("Start bot", "شروع بات"),
        ["ui.tray.stopbot"] = ("Stop bot", "توقف بات"),
        ["ui.tray.exit"] = ("Exit", "خروج"),
        ["ui.tray.online"] = ("Soul Remote — online", "سول ریموت — آنلاین"),
        ["ui.tray.offline"] = ("Soul Remote — offline", "سول ریموت — آفلاین"),
        ["ui.tray.hidden"] = ("Still running in the tray. Right-click the icon for options.", "همچنان در سینی سیستم اجرا می‌شود. برای گزینه‌ها روی نماد راست‌کلیک کنید."),
        ["ui.dialog.title"] = ("Soul Remote", "سول ریموت"),
        ["ui.dialog.errortitle"] = ("Soul Remote — error", "سول ریموت — خطا"),
        ["ui.dialog.alreadyrunning"] = ("Soul Remote is already running (check the system tray).", "سول ریموت هم‌اکنون در حال اجراست (سینی سیستم را ببینید)."),

        // ================= Errors surfaced from services =================
        ["err.nocloudflare"] = ("Cloudflare is not connected. Bring the relay up on the Connect page first.", "کلادفلر وصل نیست. ابتدا در صفحهٔ اتصال رله را بالا بیاورید."),
        ["err.notelegram"] = ("Telegram bot token is not set. Add it on the Connect page.", "توکن بات تلگرام تنظیم نشده است. آن را در صفحهٔ اتصال وارد کنید."),
        ["err.nochats"] = ("No authorized chats yet — use the pairing code from a Telegram chat to link one.", "هنوز چت مجازی وجود ندارد — با کد اتصال از یک چت تلگرام یکی را متصل کنید."),
        ["err.conflict"] = ("Another program is polling this bot token. Stop it, or give this PC its own bot.",
                            "برنامهٔ دیگری در حال دریافت از این توکن بات است. آن را متوقف کنید یا برای این رایانه بات جداگانه بسازید."),

        // ================= The "/" command list Telegram shows =================
        ["cmd.menu"] = ("Open the control panel", "باز کردن پنل کنترل"),
        ["cmd.screenshot"] = ("Send a screenshot", "ارسال اسکرین‌شات"),
        ["cmd.lock"] = ("Lock this PC", "قفل کردن این رایانه"),
        ["cmd.sysinfo"] = ("System overview", "نمای کلی سیستم"),
        ["cmd.disks"] = ("Disk usage", "مصرف دیسک"),
        ["cmd.battery"] = ("Battery and power", "باتری و برق"),
        ["cmd.network"] = ("Network addresses", "نشانی‌های شبکه"),
        ["cmd.processes"] = ("Top processes", "پرمصرف‌ترین پروسه‌ها"),
        ["cmd.volume"] = ("Set the volume", "تنظیم میزان صدا"),
        ["cmd.clipboard"] = ("Read the clipboard", "خواندن کلیپ‌بورد"),
        ["cmd.cancel"] = ("Cancel a pending shutdown", "لغو خاموشی در انتظار"),
        ["cmd.lang"] = ("Switch language", "تغییر زبان"),
        ["cmd.ping"] = ("Check the relay is alive", "بررسی زنده بودن رله"),
        ["err.cf.inactive"] = ("This Cloudflare token is not active. Create one from the \"Edit Cloudflare Workers\" template and paste it again.",
                               "این توکن کلادفلر فعال نیست. یکی با قالب «Edit Cloudflare Workers» بسازید و دوباره جای‌گذاری کنید."),
        ["err.cf.noaccount"] = ("The token works but reaches no account. Give it Account -> Workers Scripts -> Edit permission.",
                                "توکن کار می‌کند اما به هیچ حسابی نمی‌رسد. دسترسی Account ← Workers Scripts ← Edit را به آن بدهید."),
        ["err.cf.nosubdomain"] = ("This account has no workers.dev subdomain yet. Open Cloudflare -> Workers & Pages once to claim one, then run Connect again.",
                                  "این حساب هنوز زیردامنهٔ workers.dev ندارد. یک‌بار Cloudflare ← Workers & Pages را باز کنید تا ثبت شود، سپس دوباره اتصال را اجرا کنید."),
        ["err.cf.nosecret"] = ("Could not establish the proxy secret, so the worker would be deployed as an open relay. Deployment stopped.",
                               "کلید مشترک پراکسی ساخته نشد و ورکر به‌صورت رلهٔ باز مستقر می‌شد. استقرار متوقف شد."),
    };
}
