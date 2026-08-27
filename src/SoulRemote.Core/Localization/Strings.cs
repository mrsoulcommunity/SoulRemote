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

    /// <summary>
    /// The catalogue. A handful of Persian rows carry a literal <c>\u200E</c> — the
    /// left-to-right mark — immediately around a bot command or an @handle. Those
    /// tokens have to be typed back character for character, and the leading slash
    /// and at-sign are bidi-neutral: inside a Persian sentence they take the
    /// paragraph direction and land on the wrong end, so "/cmd" reads as "cmd/".
    /// The mark gives them a left-to-right neighbour to bind to. It is written as
    /// an escape rather than pasted in, because an invisible character in a string
    /// literal is one nobody can maintain.
    /// </summary>
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
        ["bot.menu.settings"] = ("⚙️ Settings", "⚙️ تنظیمات"),
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
        ["bot.capture.nodisplays"] = ("No displays were found on this machine.", "هیچ نمایشگری روی این رایانه پیدا نشد."),
        ["bot.capture.badbounds"] = ("The screen area to capture is not valid.", "محدودهٔ تصویربرداری معتبر نیست."),

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

        // Permission changes are confirmed in both directions. Turning one on hands
        // this chat more of the machine; turning one off takes away something the
        // owner may be relying on. Neither should happen on a mis-tap.
        ["bot.confirm.perm.on.toast"] = ("Confirm switching on", "تأیید روشن کردن"),
        ["bot.confirm.perm.off.toast"] = ("Confirm switching off", "تأیید خاموش کردن"),
        ["bot.confirm.shell.on.question"] = ("Let this chat run any command on this PC?",
                                             "این چت بتواند هر دستوری را روی این رایانه اجرا کند؟"),
        ["bot.confirm.shell.off.question"] = ("Stop this chat running commands?",
                                              "اجرای دستور برای این چت متوقف شود؟"),
        ["bot.confirm.files.on.question"] = ("Let this chat browse and fetch files from this PC?",
                                             "این چت بتواند فایل‌های این رایانه را مرور و دریافت کند؟"),
        ["bot.confirm.files.off.question"] = ("Stop this chat reaching files?",
                                              "دسترسی این چت به فایل‌ها قطع شود؟"),
        ["bot.confirm.typing.on.question"] = ("Let this chat type into the focused window?",
                                              "این چت بتواند در پنجرهٔ فعال تایپ کند؟"),
        ["bot.confirm.typing.off.question"] = ("Stop this chat typing?", "تایپ کردن برای این چت متوقف شود؟"),
        ["bot.confirm.revoke.toast"] = ("Confirm removing", "تأیید حذف"),
        ["bot.confirm.revoke.question"] = ("Remove <b>{0}</b>? That chat loses access to this PC.",
                                           "<b>{0}</b> حذف شود؟ آن چت دسترسی‌اش به این رایانه را از دست می‌دهد."),
        ["bot.confirm.revoke.self.question"] = ("Remove <b>{0}</b> — this chat? You will lose access from here, and pairing again needs the desktop app.",
                                                "<b>{0}</b> یعنی همین چت حذف شود؟ دسترسی‌تان از اینجا قطع می‌شود و جفت‌شدن دوباره به برنامهٔ دسکتاپ نیاز دارد."),
        ["bot.confirm.wifi.off.toast"] = ("Confirm disconnecting", "تأیید قطع اتصال"),
        ["bot.confirm.wifi.off.question"] = ("Disconnect Wi-Fi?", "وای‌فای قطع شود؟"),
        ["bot.confirm.wifi.selfwarning"] = ("⚠️ This PC reaches Telegram over Wi-Fi. Disconnecting it cuts this bot off, and only someone at the machine can reconnect it.",
                                            "⚠️ این رایانه از راه وای‌فای به تلگرام وصل است. قطع کردنش ارتباط این بات را می‌بُرد و فقط کسی که پای دستگاه است می‌تواند دوباره وصلش کند."),

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

        // ================= Telegram bot: settings =================
        ["bot.set.title"] = ("⚙️ <b>Settings</b>", "⚙️ <b>تنظیمات</b>"),
        ["bot.set.subtitle"] = ("Everything the desktop app can change, from here.",
                                "هر چیزی که برنامهٔ دسکتاپ می‌تواند تغییر دهد، از همین‌جا."),
        ["bot.set.readonly"] = ("🔒 Changing settings from Telegram is switched off in the desktop app. These are shown for reference.",
                                "🔒 تغییر تنظیمات از تلگرام در برنامهٔ دسکتاپ خاموش است. این‌ها فقط برای اطلاع نشان داده می‌شوند."),
        ["bot.set.readonly.toast"] = ("Settings are read-only from Telegram.", "تنظیمات از تلگرام فقط‌خواندنی است."),
        ["bot.set.perms"] = ("🔐 Permissions", "🔐 مجوزها"),
        ["bot.set.startup"] = ("🚀 Startup", "🚀 راه‌اندازی"),
        ["bot.set.prefs"] = ("🤖 Bot preferences", "🤖 ترجیحات بات"),
        ["bot.set.chats"] = ("👥 Paired chats", "👥 چت‌های جفت‌شده"),
        ["bot.set.windows"] = ("🖥 Windows", "🖥 ویندوز"),
        ["bot.set.stale"] = ("That list has moved on — open the screen again.",
                             "آن فهرست تازه نیست — صفحه را دوباره باز کنید."),
        ["bot.set.saved"] = ("Saved", "ذخیره شد"),
        ["bot.set.savefailed"] = ("The change did not reach disk — nothing was saved.",
                                  "تغییر روی دیسک ننشست — چیزی ذخیره نشد."),

        // Permissions
        ["bot.set.perm.title"] = ("🔐 <b>Permissions</b>", "🔐 <b>مجوزها</b>"),
        ["bot.set.perm.warning"] = ("Each of these hands this chat more of the machine. Changing one asks first.",
                                    "هرکدام از این‌ها بخش بیشتری از دستگاه را به این چت می‌سپارد. تغییر هرکدام اول تأیید می‌خواهد."),
        ["bot.set.perm.shell"] = ("Run commands", "اجرای دستور"),
        ["bot.set.perm.files"] = ("File access", "دسترسی به فایل"),
        ["bot.set.perm.typing"] = ("Type into windows", "تایپ در پنجره‌ها"),

        // Startup
        ["bot.set.startup.title"] = ("🚀 <b>Startup &amp; notifications</b>", "🚀 <b>راه‌اندازی و اطلاع‌رسانی</b>"),
        ["bot.set.startup.startwin"] = ("Start with Windows", "اجرا هنگام روشن شدن ویندوز"),
        ["bot.set.startup.autobot"] = ("Start the bot automatically", "شروع خودکار بات"),
        ["bot.set.startup.startmin"] = ("Start minimised to tray", "شروع به‌صورت کوچک‌شده در سینی"),
        ["bot.set.startup.notify"] = ("Tell me when the bot comes online", "وقتی بات آنلاین شد خبرم کن"),
        ["bot.set.startup.unmanaged"] = ("Start with Windows can only be changed in the desktop app on this build.",
                                         "در این نسخه، اجرا هنگام روشن شدن ویندوز فقط در برنامهٔ دسکتاپ تغییر می‌کند."),

        // Bot preferences
        ["bot.set.pref.title"] = ("🤖 <b>Bot preferences</b>", "🤖 <b>ترجیحات بات</b>"),
        ["bot.set.pref.poll"] = ("⏱ Poll timeout: {0}s", "⏱ مهلت دریافت: {0} ثانیه"),
        ["bot.set.pref.poll.custom"] = ("⏱ Poll timeout…", "⏱ مهلت دریافت…"),
        ["bot.set.pref.logs"] = ("🗒 Keep logs: {0} days", "🗒 نگهداری لاگ: {0} روز"),
        ["bot.set.pref.logs.forever"] = ("🗒 Keep logs: forever", "🗒 نگهداری لاگ: برای همیشه"),
        ["bot.set.pref.logs.custom"] = ("🗒 Log retention…", "🗒 مدت نگهداری لاگ…"),
        ["bot.set.pref.folder"] = ("📥 Downloads: <code>{0}</code>", "📥 دانلودها: <code>{0}</code>"),
        ["bot.set.pref.folder.default"] = ("the default Downloads folder", "پوشهٔ پیش‌فرض دانلود"),
        ["bot.set.pref.folder.set"] = ("📥 Download folder…", "📥 پوشهٔ دانلود…"),
        ["bot.set.pref.folder.reset"] = ("↩️ Use the default folder", "↩️ استفاده از پوشهٔ پیش‌فرض"),
        ["bot.set.pref.autocheck"] = ("Check for updates", "بررسی به‌روزرسانی"),
        ["bot.set.pref.autoinstall"] = ("Install updates by itself", "نصب خودکار به‌روزرسانی"),

        // Paired chats
        ["bot.set.chat.title"] = ("👥 <b>Paired chats</b>", "👥 <b>چت‌های جفت‌شده</b>"),
        ["bot.set.chat.count"] = ("{0} chat(s) can control this PC.", "{0} چت می‌تواند این رایانه را کنترل کند."),
        ["bot.set.chat.none"] = ("No chat is paired with this PC.", "هیچ چتی با این رایانه جفت نشده است."),
        ["bot.set.chat.you"] = ("{0}  ·  this chat", "{0}  ·  همین چت"),
        ["bot.set.chat.isyou"] = ("This is the chat you are using.", "این همان چتی است که از آن استفاده می‌کنید."),
        ["bot.set.chat.rename"] = ("✏️ Rename…", "✏️ تغییر نام…"),
        ["bot.set.chat.revoke"] = ("🗑 Remove access", "🗑 حذف دسترسی"),
        ["bot.set.chat.renamed"] = ("Renamed to {0}", "به {0} تغییر نام یافت"),
        ["bot.set.chat.revoked"] = ("{0} can no longer reach this PC.", "{0} دیگر به این رایانه دسترسی ندارد."),
        ["bot.set.chat.lastone"] = ("That is the only paired chat — removing it would leave the PC unreachable.",
                                    "این تنها چت جفت‌شده است — حذفش رایانه را غیرقابل‌دسترس می‌کند."),
        ["bot.set.chat.badname"] = ("Send a name with something in it.", "نامی بفرستید که خالی نباشد."),
        ["bot.set.chat.gone"] = ("That chat is no longer paired.", "آن چت دیگر جفت نیست."),

        // Windows settings
        ["bot.set.win.title"] = ("🖥 <b>Windows settings</b>", "🖥 <b>تنظیمات ویندوز</b>"),
        ["bot.set.win.unavailable"] = ("Windows settings are not available in this build.",
                                       "تنظیمات ویندوز در این نسخه در دسترس نیست."),
        ["bot.set.win.plan"] = ("⚡ Power plan", "⚡ طرح مصرف برق"),
        ["bot.set.win.brightness"] = ("☀️ Brightness", "☀️ روشنایی"),
        ["bot.set.win.wifi"] = ("📶 Wi-Fi", "📶 وای‌فای"),
        ["bot.set.win.bluetooth"] = ("🔵 Bluetooth", "🔵 بلوتوث"),

        ["bot.set.plan.title"] = ("⚡ <b>Power plan</b>", "⚡ <b>طرح مصرف برق</b>"),
        ["bot.set.plan.none"] = ("Windows reported no power plans on this machine.",
                                 "ویندوز روی این دستگاه هیچ طرح مصرفی گزارش نکرد."),

        ["bot.set.bri.title"] = ("☀️ <b>Brightness</b>", "☀️ <b>روشنایی</b>"),
        ["bot.set.bri.now"] = ("Currently {0}%.", "اکنون {0}٪."),
        ["bot.set.bri.unsupported"] = ("This machine has no panel I can dim — brightness over WMI covers built-in laptop screens only.",
                                       "این دستگاه صفحه‌ای ندارد که بتوانم کم‌نورش کنم — روشنایی از راه WMI فقط صفحه‌های داخلی لپ‌تاپ را پوشش می‌دهد."),
        ["bot.set.bri.level"] = ("{0}%", "{0}٪"),
        ["bot.set.bri.custom"] = ("🎚 Set a level…", "🎚 تنظیم دقیق…"),

        ["bot.set.wifi.title"] = ("📶 <b>Wi-Fi</b>", "📶 <b>وای‌فای</b>"),
        ["bot.set.wifi.noadapter"] = ("This machine has no wireless adapter.", "این دستگاه کارت بی‌سیم ندارد."),
        ["bot.set.wifi.connected"] = ("Connected to <b>{0}</b>.", "به <b>{0}</b> وصل است."),
        ["bot.set.wifi.connectedunknown"] = ("Connected.", "متصل است."),
        ["bot.set.wifi.disconnected"] = ("Not connected.", "متصل نیست."),
        ["bot.set.wifi.profiles"] = ("Saved networks:", "شبکه‌های ذخیره‌شده:"),
        ["bot.set.wifi.noprofiles"] = ("No saved networks on this machine.", "هیچ شبکهٔ ذخیره‌شده‌ای روی این دستگاه نیست."),
        ["bot.set.wifi.disconnect"] = ("🔌 Disconnect", "🔌 قطع اتصال"),
        ["bot.set.wifi.refresh"] = ("🔄 Refresh", "🔄 تازه‌سازی"),

        ["bot.set.bt.title"] = ("🔵 <b>Bluetooth</b>", "🔵 <b>بلوتوث</b>"),
        ["bot.set.bt.on"] = ("Bluetooth is on.", "بلوتوث روشن است."),
        ["bot.set.bt.off"] = ("Bluetooth is off.", "بلوتوث خاموش است."),
        ["bot.set.bt.none"] = ("This machine has no Bluetooth radio I can reach.",
                               "این دستگاه رادیوی بلوتوثی که بتوانم به آن برسم ندارد."),
        ["bot.set.bt.turnon"] = ("🔵 Turn on", "🔵 روشن کن"),
        ["bot.set.bt.turnoff"] = ("⚪ Turn off", "⚪ خاموش کن"),

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
        ["bot.prompt.poll"] = ("Send how long to hold each poll open, <b>5</b> to <b>50</b> seconds.",
                               "بفرستید هر بار دریافت چند ثانیه باز بماند، <b>۵</b> تا <b>۵۰</b>."),
        ["bot.prompt.logdays"] = ("Send how many days to keep logs. <b>0</b> keeps them forever.",
                                  "بفرستید لاگ‌ها چند روز نگه داشته شوند. <b>۰</b> یعنی برای همیشه."),
        ["bot.prompt.folder"] = ("Send the full path of the folder to save received files in.",
                                 "مسیر کامل پوشه‌ای که فایل‌های دریافتی در آن ذخیره شوند را بفرستید."),
        ["bot.prompt.brightness"] = ("Send a brightness from <b>0</b> to <b>100</b>.",
                                     "روشنایی را بین <b>۰</b> تا <b>۱۰۰</b> بفرستید."),
        ["bot.prompt.rename"] = ("Send a name for this chat.", "نامی برای این چت بفرستید."),
        ["bot.prompt.generic"] = ("Send a value.", "یک مقدار بفرستید."),
        ["bot.placeholder.volume"] = ("0-100", "۰ تا ۱۰۰"),
        ["bot.placeholder.kill"] = ("chrome or 1234", "chrome یا ۱۲۳۴"),
        ["bot.placeholder.open"] = ("https://…", "https://…"),
        ["bot.placeholder.shell"] = ("ipconfig /all", "ipconfig /all"),
        ["bot.placeholder.path"] = (@"C:\Users\…", @"C:\Users\…"),
        ["bot.placeholder.poll"] = ("25", "۲۵"),
        ["bot.placeholder.logdays"] = ("14", "۱۴"),
        ["bot.placeholder.folder"] = (@"C:\Users\…\Downloads", @"C:\Users\…\Downloads"),
        ["bot.placeholder.brightness"] = ("0-100", "۰ تا ۱۰۰"),
        ["bot.placeholder.rename"] = ("My phone", "گوشی من"),
        ["bot.placeholder.generic"] = ("Type here", "اینجا بنویسید"),
        ["bot.prompt.notanumber"] = ("That is not a number between 0 and 100.", "این عددی بین ۰ تا ۱۰۰ نیست."),
        ["bot.prompt.notawholenumber"] = ("That is not a whole number.", "این یک عدد درست نیست."),

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
        ["bot.chatid"] = ("Your chat ID: <code>{0}</code>", "شناسهٔ چت شما: <code>{0}</code>"),
        ["bot.pong"] = ("🏓 pong", "🏓 پونگ"),
        ["bot.nothing"] = ("Nothing to do.", "کاری برای انجام نیست."),
        ["bot.shell.off"] = ("🔒 Shell commands are switched off in the desktop app.", "🔒 اجرای دستور در برنامهٔ دسکتاپ خاموش است."),
        ["bot.shell.offtoast"] = ("Shell commands are switched off in the desktop app.", "اجرای دستور در برنامهٔ دسکتاپ خاموش است."),
        ["bot.open.weblinksonly"] = ("🔒 Only web links are allowed. To open files and folders on this PC, turn on file access in the Soul Remote app.",
                                     "🔒 فقط لینک وب مجاز است. برای باز کردن فایل و پوشه روی این رایانه، دسترسی فایل را در برنامهٔ سول ریموت روشن کنید."),
        ["bot.file.off"] = ("🔒 File access is switched off in the desktop app.", "🔒 دسترسی به فایل در برنامهٔ دسکتاپ خاموش است."),
        ["bot.type.off"] = ("🔒 Typing into the focused window is switched off in the desktop app.",
                            "🔒 تایپ در پنجرهٔ فعال در برنامهٔ دسکتاپ خاموش است."),
        ["bot.type.offtoast"] = ("Typing is switched off in the desktop app.", "تایپ در برنامهٔ دسکتاپ خاموش است."),
        ["bot.input.typingoff"] = ("Typing is switched off in the desktop app.", "تایپ در برنامهٔ دسکتاپ خاموش است."),
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
        ["act.set.on"] = ("{0} is on.", "{0} روشن است."),
        ["act.set.off"] = ("{0} is off.", "{0} خاموش است."),
        ["act.set.poll"] = ("Poll timeout set to {0} seconds.", "مهلت دریافت روی {0} ثانیه تنظیم شد."),
        ["act.set.logdays"] = ("Logs will be kept for {0} days.", "لاگ‌ها {0} روز نگه داشته می‌شوند."),
        ["act.set.logforever"] = ("Logs will be kept indefinitely.", "لاگ‌ها بدون محدودیت زمانی نگه داشته می‌شوند."),
        ["act.set.folder"] = ("Received files will be saved to {0}.", "فایل‌های دریافتی در {0} ذخیره می‌شوند."),
        ["act.set.folderdefault"] = ("Received files will go to the default Downloads folder.",
                                     "فایل‌های دریافتی به پوشهٔ پیش‌فرض دانلود می‌روند."),
        ["act.set.foldermissing"] = ("There is no folder at that path on this PC.",
                                     "در آن مسیر روی این رایانه پوشه‌ای نیست."),
        ["act.plan.set"] = ("Power plan switched to {0}.", "طرح مصرف برق به {0} تغییر کرد."),
        ["act.plan.unknown"] = ("That is not a power plan on this machine.", "این طرح مصرفی روی این دستگاه نیست."),
        ["act.plan.failed"] = ("Windows would not switch the power plan.", "ویندوز طرح مصرف برق را تغییر نداد."),
        ["act.bri.set"] = ("Brightness set to {0}%.", "روشنایی روی {0}٪ تنظیم شد."),
        ["act.bri.unsupported"] = ("No panel on this machine reports a brightness control.",
                                   "هیچ صفحه‌ای روی این دستگاه کنترل روشنایی گزارش نمی‌کند."),
        ["act.bri.failed"] = ("Windows would not change the brightness.", "ویندوز روشنایی را تغییر نداد."),
        ["act.wifi.connecting"] = ("Connecting to {0}…", "در حال اتصال به {0}…"),
        ["act.wifi.disconnected"] = ("Wi-Fi disconnected.", "وای‌فای قطع شد."),
        ["act.wifi.nointerface"] = ("This machine has no wireless adapter.", "این دستگاه کارت بی‌سیم ندارد."),
        ["act.wifi.noprofile"] = ("There is no saved network by that name.", "شبکهٔ ذخیره‌شده‌ای با آن نام نیست."),
        ["act.wifi.failed"] = ("Windows would not change the Wi-Fi connection.", "ویندوز اتصال وای‌فای را تغییر نداد."),
        ["act.bt.on"] = ("Bluetooth turned on.", "بلوتوث روشن شد."),
        ["act.bt.off"] = ("Bluetooth turned off.", "بلوتوث خاموش شد."),
        ["act.bt.none"] = ("This machine has no Bluetooth radio.", "این دستگاه رادیوی بلوتوث ندارد."),
        ["act.bt.denied"] = ("Windows refused access to the Bluetooth radio.",
                             "ویندوز دسترسی به رادیوی بلوتوث را نپذیرفت."),
        ["act.bt.failed"] = ("Windows would not change the Bluetooth radio.", "ویندوز رادیوی بلوتوث را تغییر نداد."),
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
        ["ui.chrome.restore"] = ("Restore", "بازگرداندن"),

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
        ["ui.dash.test.nochats"] = ("Pair a Telegram chat first — send /pair with the code below.", "ابتدا یک چت تلگرام را متصل کنید — \u200E/pair\u200E را با کد زیر بفرستید."),
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
        ["ui.connect.tg.hint.new"] = ("Ask @BotFather for /newbot, then paste the HTTP API token.", "از \u200E@BotFather\u200E دستور \u200E/newbot\u200E را بگیرید و توکن HTTP API را جای‌گذاری کنید."),
        ["ui.connect.tg.hint.saved"] = ("A bot token is already saved. Leave this blank to keep it, or paste a new one to replace it.",
                                        "یک توکن بات ذخیره شده است. برای نگه داشتنش این را خالی بگذارید یا توکن تازه‌ای جای‌گذاری کنید."),
        ["ui.connect.tg.open"] = ("Open BotFather", "باز کردن BotFather"),
        ["ui.connect.go"] = ("Connect", "اتصال"),
        ["ui.connect.cancel"] = ("Cancel", "انصراف"),
        ["ui.connect.sequence"] = ("BRING-UP SEQUENCE", "مراحل راه‌اندازی"),
        ["ui.connect.endpoint"] = ("RELAY ENDPOINT", "نشانی رله"),
        ["ui.connect.working"] = ("Bringing the relay up…", "در حال بالا آوردن رله…"),
        ["ui.connect.done"] = ("Connected as @{0}. Open Telegram and send /pair with the code on the dashboard.",
                               "با \u200E@{0}\u200E متصل شد. تلگرام را باز کنید و \u200E/pair\u200E را با کد داشبورد بفرستید."),
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
        ["ui.step.workerstale"] = ("Edge answered with worker v{0}, expected v{1} — run Connect again",
                                   "لبه با ورکر نسخهٔ {0} پاسخ داد، نسخهٔ {1} انتظار می‌رفت — دوباره اتصال را اجرا کنید"),
        ["ui.step.listening"] = ("Listening for commands", "در انتظار دستور"),
        ["ui.step.cancelled"] = ("Cancelled", "لغو شد"),

        // ================= Desktop: settings =================
        ["ui.settings.title"] = ("Settings", "تنظیمات"),
        ["ui.settings.autosave"] = ("Changes save as you make them.", "تغییرات همان لحظه ذخیره می‌شوند."),
        ["ui.settings.saved"] = ("Saved {0}", "ذخیره شد {0}"),
        ["ui.settings.savefailed"] = ("Not saved — see Activity for the reason.", "ذخیره نشد — دلیلش را در «فعالیت» ببینید."),
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
        ["ui.settings.remote.label"] = ("Let a paired chat change settings from Telegram",
                                        "چت متصل بتواند تنظیمات را از تلگرام تغییر دهد"),
        ["ui.settings.remote.hint"] = ("The Settings section in the bot. Turn this off and it becomes read-only — this is the one switch Telegram cannot reach, so it is the way back if a paired chat is ever taken over.",
                                       "بخش تنظیمات در بات. با خاموش کردنش فقط‌خواندنی می‌شود — این تنها کلیدی است که تلگرام به آن دسترسی ندارد، پس اگر روزی چت متصلی از دست رفت، راه بازگشت همین است."),
        ["ui.settings.shell.hint"] = ("Lets a paired chat run any command on this PC. Leave off unless you need it.",
                                      "به چت متصل اجازه می‌دهد هر دستوری را روی این رایانه اجرا کند. تا وقتی لازم نیست خاموش بماند."),
        ["ui.settings.files.hint"] = ("Lets a paired chat browse folders, fetch files and open local paths.",
                                      "به چت متصل اجازه می‌دهد پوشه‌ها را مرور کند، فایل بگیرد و مسیرهای محلی را باز کند."),
        ["ui.settings.interface"] = ("INTERFACE", "رابط کاربری"),
        ["ui.settings.reducemotion"] = ("Reduce motion", "کاهش حرکت"),
        ["ui.settings.reducemotion.hint"] = ("Stops the relay line animating.", "انیمیشن خط رله را متوقف می‌کند."),
        ["ui.settings.polltimeout"] = ("Long-poll timeout (seconds)", "مهلت long-poll (ثانیه)"),
        ["ui.settings.polltimeout.hint"] = ("How long each getUpdates call waits. 25 suits most networks; lower it on a flaky link.",
                                            "هر فراخوانی getUpdates چقدر منتظر بماند. ۲۵ برای بیشتر شبکه‌ها مناسب است؛ روی پیوند ناپایدار کمترش کنید."),
        ["ui.settings.logretention"] = ("Keep activity logs for (days)", "نگهداری گزارش فعالیت (روز)"),
        ["ui.settings.logretention.hint"] = ("Older log files are deleted when the app starts.", "فایل‌های گزارش قدیمی‌تر هنگام اجرای برنامه حذف می‌شوند."),
        ["ui.settings.openlogs"] = ("Open log folder", "باز کردن پوشهٔ گزارش"),
        ["ui.settings.opensettings"] = ("Open settings folder", "باز کردن پوشهٔ تنظیمات"),
        ["ui.settings.permissions.hint"] = ("Power, screenshot, media and process controls are always available to paired chats. These two are off until you turn them on.",
                                            "کنترل برق، اسکرین‌شات، رسانه و پروسه‌ها همیشه در دسترس چت‌های متصل است. این دو تا وقتی روشنشان نکنید خاموش‌اند."),
        ["ui.settings.shell.label"] = ("Allow shell commands (/cmd)", "اجازهٔ اجرای دستور (\u200E/cmd\u200E)"),
        ["ui.settings.files.label"] = ("Allow browsing and fetching files", "اجازهٔ مرور و دریافت فایل"),
        ["ui.settings.typing.label"] = ("Allow typing into the focused window", "اجازهٔ تایپ در پنجرهٔ فعال"),
        ["ui.settings.typing.hint"] = ("Lets a paired chat send keystrokes to whatever is in front on this PC — including a terminal.",
                                       "به چت متصل اجازه می‌دهد به هر چیزی که روی این رایانه در پیش‌زمینه است کلید بفرستد — از جمله ترمینال."),
        ["ui.settings.onthispc"] = ("ON THIS PC", "روی این رایانه"),
        ["ui.settings.dpapi"] = ("Tokens in this file are encrypted with Windows DPAPI and can only be read by your account on this machine.",
                                 "توکن‌های این فایل با DPAPI ویندوز رمزنگاری شده‌اند و فقط با حساب شما روی همین رایانه خوانده می‌شوند."),
        ["ui.settings.downloads"] = ("Files received from Telegram", "فایل‌های دریافتی از تلگرام"),
        ["ui.settings.downloads.hint"] = ("Where a file sent to the bot is saved. Leave blank for Downloads\\Soul Remote.",
                                          "فایلی که به بات فرستاده می‌شود کجا ذخیره شود. برای Downloads\\Soul Remote خالی بگذارید."),
        ["ui.settings.english"] = ("English", "English"),
        ["ui.settings.persian"] = ("فارسی", "فارسی"),

        // ================= Desktop: updates =================
        ["ui.settings.updates"] = ("UPDATES", "به‌روزرسانی"),
        ["ui.settings.update.auto"] = ("Check GitHub for new versions", "بررسی نسخه‌های تازه در گیت‌هاب"),
        ["ui.settings.update.auto.hint"] = ("One request when the app starts and once a day after that. It sends nothing about this PC.",
                                            "یک درخواست هنگام اجرای برنامه و پس از آن روزی یک بار. هیچ چیزی دربارهٔ این رایانه فرستاده نمی‌شود."),
        ["ui.settings.update.autoinstall"] = ("Install new versions on their own", "نصب خودکار نسخه‌های تازه"),
        ["ui.settings.update.autoinstall.hint"] = ("The installer runs in the background and Soul Remote restarts itself. Only ever applied when the published SHA-256 matches.",
                                                   "نصب‌کننده در پس‌زمینه اجرا می‌شود و سول ریموت خودش دوباره بالا می‌آید. فقط وقتی اعمال می‌شود که SHA-256 منتشرشده بخواند."),
        ["ui.settings.update.check"] = ("Check now", "بررسی کن"),
        ["ui.settings.update.install"] = ("Install now", "همین حالا نصب کن"),
        ["ui.settings.update.notes"] = ("What changed", "چه چیزی تغییر کرده"),
        ["ui.update.eyebrow"] = ("UPDATE", "به‌روزرسانی"),
        ["ui.update.later"] = ("Not now", "الان نه"),
        ["ui.update.badge"] = ("Update available", "نسخهٔ تازه موجود است"),
        ["ui.update.headline"] = ("Soul Remote {0} is out", "سول ریموت {0} منتشر شد"),
        ["ui.update.idle"] = ("Soul Remote {0}", "سول ریموت {0}"),
        ["ui.update.checking"] = ("Looking for a newer version…", "در حال جست‌وجوی نسخهٔ تازه‌تر…"),
        ["ui.update.uptodate"] = ("Soul Remote {0} is the newest version.", "سول ریموت {0} تازه‌ترین نسخه است."),
        ["ui.update.available"] = ("Version {0} is out.", "نسخهٔ {0} منتشر شده است."),
        ["ui.update.downloading"] = ("Downloading {0} — {1}%", "در حال دریافت {0} — {1}%"),
        ["ui.update.ready"] = ("Version {0} is downloaded and verified.", "نسخهٔ {0} دریافت و بررسی شد."),
        ["ui.update.installing"] = ("Installing {0}. Soul Remote will close and come back on its own.",
                                    "در حال نصب {0}. سول ریموت بسته می‌شود و خودش برمی‌گردد."),
        ["ui.update.failed"] = ("Update: {0}", "به‌روزرسانی: {0}"),
        ["ui.update.portable"] = ("This copy was not put here by the installer, so it cannot replace itself. Download the new version from the release page.",
                                  "این نسخه را نصب‌کننده اینجا نگذاشته است، پس نمی‌تواند خودش را جایگزین کند. نسخهٔ تازه را از صفحهٔ انتشار دریافت کنید."),
        ["ui.update.stale"] = ("Version {0} is already installed on this PC, but this copy still reports an older version. Reinstall it from the release page.",
            "نسخهٔ {0} از قبل روی این رایانه نصب شده، ولی این نسخه هنوز خودش را قدیمی‌تر گزارش می‌کند. آن را از صفحهٔ انتشار دوباره نصب کنید."),
        ["ui.update.restarted"] = ("Updated to {0}.", "به {0} به‌روزرسانی شد."),

        // ================= Desktop: activity log =================
        ["ui.logs.title"] = ("Activity", "فعالیت"),
        ["ui.logs.subtitle"] = ("Everything the relay has done since the app started.", "هر کاری که رله از زمان اجرای برنامه انجام داده است."),
        ["ui.logs.all"] = ("All", "همه"),
        ["ui.logs.problems"] = ("Problems", "مشکل‌ها"),
        ["ui.logs.errors"] = ("Errors", "خطاها"),
        ["ui.logs.clear"] = ("Clear", "پاک کردن"),
        ["ui.logs.empty"] = ("Nothing logged yet.", "هنوز چیزی ثبت نشده است."),
        ["ui.logs.clear.confirm"] = ("Clear the activity log? This also deletes the log files on disk, which name your paired chats and relay address.",
                                     "گزارش فعالیت پاک شود؟ فایل‌های گزارش روی دیسک هم حذف می‌شوند — همان‌هایی که چت‌های متصل و نشانی رله را نام می‌برند."),

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
        ["cmd.settings"] = ("Open settings", "باز کردن تنظیمات"),
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
