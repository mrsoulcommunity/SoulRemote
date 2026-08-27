using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SoulRemote.Abstractions;
using SoulRemote.Localization;
using SoulRemote.Models;

namespace SoulRemote.Services;

/// <summary>
/// Turns Telegram updates into actions on this machine.
///
/// The bot is button-driven: a tap on an inline keyboard edits the panel message
/// into the next screen and reports the result as a toast, so the chat does not
/// fill up with replies. Values that cannot be expressed as a button (a volume
/// level, a process name) are collected with a one-shot prompt. Typed commands
/// still work for anyone who prefers them.
/// </summary>
public sealed class CommandRouter
{
    private const int ShutdownDelaySeconds = 15;
    private const int MaxPairAttemptsPerChat = 5;

    /// <summary>How long a pairing code stays usable once it is shown.</summary>
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(10);

    private readonly ISettingsService _settings;
    private readonly ITelegramClient _telegram;
    private readonly ISystemControlService _system;
    private readonly IScreenshotService _screenshot;
    private readonly ISystemInfoService _info;
    private readonly ILogService _log;
    private readonly IClock _clock;
    private readonly IPcSettingsService? _pc;
    private readonly IStartupManager? _startup;

    /// <summary>
    /// The premium-emoji look. Only read here — the client applies it — but the
    /// settings screen has to say whether Telegram is actually honouring it.
    /// </summary>
    private readonly IEmojiStyler? _emoji;
    private readonly ChatPrompts _prompts;
    private readonly FileBrowser _files = new();

    /// <summary>Applies premium emoji packs. Shared, in behaviour, with the desktop page.</summary>
    private readonly EmojiImporter _emojiImporter;

    /// <summary>The Wi-Fi profile list each chat is currently looking at.</summary>
    private readonly ChoiceCache _wifiList = new();

    // Paired chats get a generous budget — enough that a person never notices it, low
    // enough that a stuck button cannot queue a hundred shutdowns. Strangers get a
    // tight one, because every reply to them is an outbound relay call we pay for.
    private readonly RateLimiter _commandLimit;
    private readonly RateLimiter _strangerLimit;

    private readonly object _pairingLock = new();
    private string _pairingCode = string.Empty;
    private DateTime _pairingIssuedAt;
    private readonly Dictionary<long, int> _failedPairAttempts = new();

    private int _commandsHandled;

    /// <summary>One-time pairing code shown in the desktop app. Setting it clears the lockout.</summary>
    public string PairingCode
    {
        get { lock (_pairingLock) return _pairingCode; }
        set
        {
            lock (_pairingLock)
            {
                _pairingCode = value ?? string.Empty;
                _pairingIssuedAt = _clock.UtcNow;
                _failedPairAttempts.Clear();
            }
        }
    }

    /// <summary>Raised (on a background thread) when a new chat is authorized via pairing.</summary>
    public event Action<long>? ChatAuthorized;

    /// <summary>Raised (on a background thread) after each accepted action, with its name.</summary>
    public event Action<string>? CommandHandled;

    /// <summary>Raised when a chat switches the language, so the desktop can follow.</summary>
    public event Action<AppLanguage>? LanguageChanged;

    public int CommandsHandled => _commandsHandled;

    /// <param name="pc">
    /// Windows' own settings. Null on a build that has no Windows half — the core
    /// tests, chiefly — and the Windows screen then says so rather than throwing.
    /// </param>
    /// <param name="startup">
    /// Writes the sign-in entry. Null hides the Start-with-Windows toggle instead of
    /// offering one that would store a flag nothing acts on.
    /// </param>
    /// <param name="emoji">
    /// The premium-emoji look, so its screen can report whether Telegram is honouring
    /// it. Null simply leaves that line reading "not tried yet".
    /// </param>
    public CommandRouter(
        ISettingsService settings, ITelegramClient telegram, ISystemControlService system,
        IScreenshotService screenshot, ISystemInfoService info, ILogService log, IClock? clock = null,
        IPcSettingsService? pc = null, IStartupManager? startup = null, IEmojiStyler? emoji = null)
    {
        _settings = settings;
        _telegram = telegram;
        _system = system;
        _screenshot = screenshot;
        _info = info;
        _log = log;
        _clock = clock ?? SystemClock.Instance;
        _pc = pc;
        _startup = startup;
        _emoji = emoji;
        _emojiImporter = new EmojiImporter(settings, telegram);
        _prompts = new ChatPrompts(_clock);
        _commandLimit = new RateLimiter(20, TimeSpan.FromSeconds(10), _clock);
        _strangerLimit = new RateLimiter(3, TimeSpan.FromMinutes(1), _clock);
    }

    public async Task HandleUpdateAsync(TgUpdate update, CancellationToken ct)
    {
        try
        {
            if (update.CallbackQuery is { } cb)
                await HandleTapAsync(cb, ct).ConfigureAwait(false);
            else if (update.Message is { } msg)
                await HandleMessageAsync(msg, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error("Error handling update", ex);
        }
    }

    // ============================================================
    // Button taps
    // ============================================================

    private async Task HandleTapAsync(TgCallbackQuery cb, CancellationToken ct)
    {
        var chatId = cb.Message?.Chat?.Id ?? cb.From?.Id ?? 0;
        var messageId = cb.Message?.MessageId ?? 0;
        var data = cb.Data ?? string.Empty;

        if (chatId == 0 || !IsAuthorized(chatId))
        {
            await _telegram.AnswerCallbackAsync(cb.Id, Strings.Get("bot.notauthorized"), true, ct).ConfigureAwait(false);
            return;
        }
        if (!_commandLimit.Allow(chatId))
        {
            await _telegram.AnswerCallbackAsync(cb.Id, Strings.Get("bot.ratelimited"), true, ct).ConfigureAwait(false);
            return;
        }

        Count(data);
        _log.Info($"Tap '{data}' from chat {chatId}.");

        var (kind, value) = Split(data);
        switch (kind)
        {
            case "m":
                // The toast is answered first, and unconditionally: Telegram spins the
                // button until the query is answered, and an edit that fails (the panel
                // was deleted, or is too old to edit) must not leave it spinning.
                await _telegram.AnswerCallbackAsync(cb.Id, null, false, ct).ConfigureAwait(false);
                await ShowScreenAsync(chatId, messageId, value, ct).ConfigureAwait(false);
                break;

            case "c":
                // A settings confirmation is still a settings write: refuse it here as
                // well, or the read-only switch would only be enforced one tap later.
                if (IsSettingsConfirm(value) && !await AllowSettingsWriteAsync(cb.Id, ct).ConfigureAwait(false))
                    break;
                var confirm = ConfirmationFor(value, chatId);
                await _telegram.AnswerCallbackAsync(cb.Id, confirm.Toast, false, ct).ConfigureAwait(false);
                await RenderAsync(chatId, messageId,
                    BotMenu.Confirm(value, confirm.Question, confirm.Cancel, confirm.Note), ct).ConfigureAwait(false);
                break;

            case "y":
                await RunConfirmedAsync(cb.Id, chatId, messageId, value, ct).ConfigureAwait(false);
                break;

            case "i":
                await AskForValueAsync(cb.Id, chatId, value, ct).ConfigureAwait(false);
                break;

            case "x":
                _prompts.Clear(chatId);
                await _telegram.AnswerCallbackAsync(cb.Id, Strings.Get("bot.prompt.cancelled"), false, ct).ConfigureAwait(false);
                await ShowScreenAsync(chatId, messageId, "home", ct).ConfigureAwait(false);
                break;

            case "a":
                await RunActionAsync(cb.Id, chatId, value, ct).ConfigureAwait(false);
                break;

            case "s":
                await RunSettingsActionAsync(cb.Id, chatId, messageId, value, ct).ConfigureAwait(false);
                break;

            case "l":
                await SwitchLanguageAsync(cb.Id, chatId, messageId, value, ct).ConfigureAwait(false);
                break;

            case "f":
                await OpenListingEntryAsync(cb.Id, chatId, messageId, value, ct).ConfigureAwait(false);
                break;

            default:
                await _telegram.AnswerCallbackAsync(cb.Id, null, false, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Renders a screen in place when possible, otherwise as a new panel.</summary>
    private async Task ShowScreenAsync(long chatId, long messageId, string screen, CancellationToken ct)
    {
        // Most screens are a pure function of the settings we already hold. The four
        // Windows ones have to ask the machine first, so they are resolved separately
        // rather than making every caller of ScreenFor asynchronous.
        var view = await AsyncScreenFor(chatId, screen, ct).ConfigureAwait(false)
                   ?? ScreenFor(chatId, screen);
        await RenderAsync(chatId, messageId, view, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The screens that read Windows' own configuration, or null when the key is not
    /// one of them. Every probe is wrapped: a subsystem that throws produces a screen
    /// saying so, with a way back, rather than an unhandled failure to render.
    /// </summary>
    private async Task<BotMenu.Screen?> AsyncScreenFor(long chatId, string screen, CancellationToken ct)
    {
        if (_pc is null || screen is not ("spln" or "sbri" or "swif" or "sblu"))
            return null;

        var writable = CanWriteSettings;
        try
        {
            switch (screen)
            {
                case "spln":
                    return BotMenu.PowerPlans(await _pc.GetPowerPlansAsync(ct).ConfigureAwait(false), writable);

                case "sbri":
                    return BotMenu.Brightness(_pc.GetBrightness(), writable);

                case "swif":
                    var state = await _pc.GetWifiAsync(ct).ConfigureAwait(false);
                    // Profiles are only listed when they can be acted on, and the list
                    // is cached as it is rendered so "s:wfc.<i>" has something to mean.
                    IReadOnlyList<string> profiles = Array.Empty<string>();
                    if (writable && state.AdapterPresent)
                    {
                        profiles = await _pc.GetWifiProfilesAsync(ct).ConfigureAwait(false);
                        _wifiList.Put(chatId, profiles);
                    }
                    return BotMenu.Wifi(state, profiles, writable);

                case "sblu":
                    return BotMenu.Bluetooth(await _pc.GetBluetoothAsync(ct).ConfigureAwait(false), writable);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning($"Could not read Windows settings for '{screen}': {ex.Message}");
            return BotMenu.Unavailable(ex.Message, "m:swin");
        }
        return null;
    }

    /// <summary>Whether the desktop app currently lets a chat change settings.</summary>
    private bool CanWriteSettings => _settings.Current.AllowRemoteSettings;

    /// <summary>
    /// Puts a screen on the chat. Editing the existing panel is preferred so the chat
    /// does not fill with menus, but a panel can become un-editable — deleted by the
    /// user, or simply too old — and in that case the screen is sent as a new message
    /// rather than lost.
    /// </summary>
    private async Task RenderAsync(long chatId, long messageId, BotMenu.Screen view, CancellationToken ct)
    {
        if (messageId > 0)
        {
            try
            {
                await _telegram.EditMessageAsync(chatId, messageId, view.Text, view.Keyboard, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Debug($"Panel {messageId} could not be edited ({ex.Message}); sending a fresh one.");
            }
        }
        await _telegram.SendMessageAsync(chatId, view.Text, view.Keyboard, ct).ConfigureAwait(false);
    }

    private BotMenu.Screen ScreenFor(long chatId, string screen)
    {
        var settings = _settings.Current;
        var writable = CanWriteSettings;

        // One paired chat, addressed by its id: "m:sch.<chatId>".
        if (screen.StartsWith("sch.", StringComparison.Ordinal))
        {
            if (!long.TryParse(screen[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)
                || !settings.AuthorizedChatIds.Contains(target))
                return BotMenu.Unavailable(Strings.Get("bot.set.chat.gone"), "m:scht");
            return BotMenu.Chat(target, settings.NameFor(target), target == chatId, writable);
        }

        // One page of the converted-emoji list: "m:seml.<page>".
        if (screen.StartsWith("seml", StringComparison.Ordinal))
        {
            var page = 0;
            if (screen.Length > 5)
                int.TryParse(screen[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out page);
            return BotMenu.PremiumEmojiList(settings, page, writable);
        }

        return screen switch
        {
            "cap" => BotMenu.Capture(SafeScreenCount()),
            "pwr" => BotMenu.Power(),
            "aud" => BotMenu.Audio(),
            "inp" => BotMenu.Input(settings.AllowInputInjection),
            "sys" => BotMenu.System(),
            "prc" => BotMenu.Processes(settings.AllowShellCommands),
            "fil" => BotMenu.Files(settings.AllowFileAccess),
            "set" => BotMenu.Settings(writable),
            "sper" => BotMenu.Permissions(settings, writable),
            "sst" => BotMenu.Startup(settings, writable, _startup is not null),
            "sbot" => BotMenu.BotPrefs(settings, writable),
            "scht" => BotMenu.Chats(settings, chatId, writable),
            "semj" => BotMenu.PremiumEmoji(settings, _emoji?.State ?? PremiumEmojiState.Unknown, writable),
            "swin" => BotMenu.WindowsSettings(_pc is not null),
            // The Windows leaves are resolved by AsyncScreenFor; reaching here means
            // there is no Windows half, so say that rather than rendering an empty one.
            "spln" or "sbri" or "swif" or "sblu"
                => BotMenu.Unavailable(Strings.Get("bot.set.win.unavailable"), "m:set"),
            _ => BotMenu.Home(Environment.MachineName, HomeStatus(), settings.AllowFileAccess),
        };
    }

    /// <summary>
    /// The one-glance answer to "how is my PC?", shown on the home panel so opening the
    /// bot is informative on its own. Every part is best-effort: a probe that fails is
    /// left out rather than breaking the panel.
    /// </summary>
    private string HomeStatus()
    {
        var parts = new List<string>();
        try
        {
            parts.Add("⏱ " + Strings.Format("bot.home.up",
                TextUtil.HumanDuration(TimeSpan.FromMilliseconds(Environment.TickCount64))));
        }
        catch { /* never block the panel on telemetry */ }

        try
        {
            if (_system.IsMuted() == true)
                parts.Add("🔇 " + Strings.Get("bot.home.muted"));
            else if (_system.GetVolumePercent() is { } percent)
                parts.Add("🔊 " + Strings.Format("bot.home.volume", percent));
        }
        catch { /* audio endpoint may be absent */ }

        try
        {
            parts.Add("👤 " + Strings.Format("bot.home.paired", _settings.Current.AuthorizedChatIds.Count));
        }
        catch { /* settings unavailable */ }

        var line = parts.Count > 0 ? string.Join("   ·   ", parts) : Strings.Get("bot.home.hint");
        return line + $"\n<i>{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}</i>";
    }

    private int SafeScreenCount()
    {
        try { return _screenshot.ScreenCount; }
        catch { return 1; }
    }

    /// <summary>What a confirmation says, and where backing out of it goes.</summary>
    private readonly record struct Confirmation(string Toast, string Question, string Cancel, string? Note = null);

    /// <summary>
    /// The confirmations that change settings rather than acting on the machine. They
    /// answer to the remote-settings switch; the power ones do not.
    /// </summary>
    private static bool IsSettingsConfirm(string action) =>
        action.StartsWith("p.", StringComparison.Ordinal)
        || action.StartsWith("cr.", StringComparison.Ordinal)
        || action is "wd" or "ecl";

    private Confirmation ConfirmationFor(string action, long chatId)
    {
        // Permission toggles: "p.<what>.<0|1>". The wording differs by direction —
        // granting and withdrawing are not the same decision.
        if (action.StartsWith("p.", StringComparison.Ordinal))
        {
            var parts = action.Split('.');
            var on = parts.Length > 2 && parts[2] == "1";
            var question = parts[1] switch
            {
                "shell" => on ? "bot.confirm.shell.on.question" : "bot.confirm.shell.off.question",
                "file" => on ? "bot.confirm.files.on.question" : "bot.confirm.files.off.question",
                "inp" => on ? "bot.confirm.typing.on.question" : "bot.confirm.typing.off.question",
                _ => "bot.confirm.generic.question",
            };
            return new Confirmation(
                Strings.Get(on ? "bot.confirm.perm.on.toast" : "bot.confirm.perm.off.toast"),
                Strings.Get(question), "m:sper");
        }

        if (action.StartsWith("cr.", StringComparison.Ordinal))
        {
            var isSelf = long.TryParse(action[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                         && id == chatId;
            var name = id == 0 ? string.Empty : _settings.Current.NameFor(id);
            return new Confirmation(
                Strings.Get("bot.confirm.revoke.toast"),
                Strings.Format(isSelf ? "bot.confirm.revoke.self.question" : "bot.confirm.revoke.question",
                    TextUtil.Html(name)),
                "m:scht");
        }

        if (action == "ecl")
        {
            // Confirmed because an imported pack is not something the user can put back
            // with an undo: getting it here took finding the pack and sending it.
            return new Confirmation(
                Strings.Get("bot.confirm.emoji.clear.toast"),
                Strings.Get("bot.confirm.emoji.clear.question"),
                "m:semj");
        }

        if (action == "wd")
        {
            // The warning is unconditional. Working out whether *this* adapter carries
            // the relay means guessing at routing that can change between the guess and
            // the tap, and a warning that is sometimes absent is worse than one that is
            // always there: the consequence is unrecoverable either way.
            return new Confirmation(
                Strings.Get("bot.confirm.wifi.off.toast"),
                Strings.Get("bot.confirm.wifi.off.question"),
                "m:swif",
                Strings.Get("bot.confirm.wifi.selfwarning"));
        }

        return action switch
        {
            "sd" => new(Strings.Get("bot.confirm.shutdown.toast"), Strings.Get("bot.confirm.shutdown.question"), "m:pwr"),
            "rs" => new(Strings.Get("bot.confirm.restart.toast"), Strings.Get("bot.confirm.restart.question"), "m:pwr"),
            "lo" => new(Strings.Get("bot.confirm.signout.toast"), Strings.Get("bot.confirm.signout.question"), "m:pwr"),
            "hb" => new(Strings.Get("bot.confirm.hibernate.toast"), Strings.Get("bot.confirm.hibernate.question"), "m:pwr"),
            _ => new(Strings.Get("bot.confirm.generic.toast"), Strings.Get("bot.confirm.generic.question"), "m:pwr"),
        };
    }

    private async Task RunConfirmedAsync(string callbackId, long chatId, long messageId, string action, CancellationToken ct)
    {
        if (IsSettingsConfirm(action))
        {
            // Re-checked rather than trusted: the confirm screen may have been rendered
            // before the desktop switched remote settings off, and its Yes button is
            // still live in the chat.
            if (!await AllowSettingsWriteAsync(callbackId, ct).ConfigureAwait(false))
                return;
            await RunConfirmedSettingAsync(callbackId, chatId, messageId, action, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            string result;
            switch (action)
            {
                case "sd": result = await _system.ShutdownAsync(ShutdownDelaySeconds).ConfigureAwait(false); break;
                case "rs": result = await _system.RestartAsync(ShutdownDelaySeconds).ConfigureAwait(false); break;
                case "lo": result = await _system.LogoffAsync().ConfigureAwait(false); break;
                case "hb": result = _system.Hibernate(); break;
                default: result = Strings.Get("bot.nothing"); break;
            }
            await _telegram.AnswerCallbackAsync(callbackId, result, true, ct).ConfigureAwait(false);
            await RenderAsync(chatId, messageId,
                new BotMenu.Screen(Strings.Format("bot.ok", TextUtil.Html(result)), BotMenu.Power().Keyboard), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _telegram.AnswerCallbackAsync(callbackId, ex.Message, true, ct).ConfigureAwait(false);
        }
    }

    private async Task AskForValueAsync(string callbackId, long chatId, string value, CancellationToken ct)
    {
        // "rn.<chatId>" is the one prompt aimed at something: the answer arrives as an
        // ordinary message with no payload, so the target rides on the prompt itself.
        string? arg = null;
        if (value.StartsWith("rn.", StringComparison.Ordinal))
        {
            arg = value[3..];
            value = "rn";
        }
        // "eone.<index>" is the same shape: the premium version of one named emoji.
        else if (value.StartsWith("eone.", StringComparison.Ordinal))
        {
            arg = value[5..];
            value = "eone";
        }

        var kind = value switch
        {
            "vol" => PromptKind.Volume,
            "kill" => PromptKind.KillProcess,
            "clip" => PromptKind.Clipboard,
            "type" => PromptKind.TypeText,
            "say" => PromptKind.Speak,
            "open" => PromptKind.OpenLink,
            "cmd" => PromptKind.ShellCommand,
            "path" => PromptKind.Path,
            "get" => PromptKind.GetFile,
            "poll" => PromptKind.PollTimeout,
            "logd" => PromptKind.LogRetention,
            "dlf" => PromptKind.DownloadFolder,
            "bri" => PromptKind.Brightness,
            "rn" => PromptKind.RenameChat,
            "epk" => PromptKind.EmojiPack,
            "eadd" => PromptKind.PremiumEmoji,
            "eone" => PromptKind.PremiumEmojiFor,
            _ => PromptKind.None,
        };
        if (kind == PromptKind.None)
        {
            await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
            return;
        }
        if (ChatPrompts.IsSettingsPrompt(kind)
            && !await AllowSettingsWriteAsync(callbackId, ct).ConfigureAwait(false))
            return;
        if (kind == PromptKind.ShellCommand && !_settings.Current.AllowShellCommands)
        {
            await _telegram.AnswerCallbackAsync(callbackId, Strings.Get("bot.shell.offtoast"), true, ct).ConfigureAwait(false);
            return;
        }
        if (kind is PromptKind.Path or PromptKind.GetFile && !_settings.Current.AllowFileAccess)
        {
            await _telegram.AnswerCallbackAsync(callbackId, Strings.Get("bot.files.off"), true, ct).ConfigureAwait(false);
            return;
        }
        if (kind == PromptKind.TypeText && !_settings.Current.AllowInputInjection)
        {
            await _telegram.AnswerCallbackAsync(callbackId, Strings.Get("bot.type.offtoast"), true, ct).ConfigureAwait(false);
            return;
        }

        await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
        await AskInChatAsync(chatId, kind, ct, arg).ConfigureAwait(false);
    }

    private async Task RunActionAsync(string callbackId, long chatId, string value, CancellationToken ct)
    {
        // Captures and reports arrive as their own message; everything else is a toast.
        switch (value)
        {
            case "ss":
                await _telegram.AnswerCallbackAsync(callbackId, Strings.Get("bot.capture.working"), false, ct).ConfigureAwait(false);
                await SendScreenshotAsync(chatId, null, ct).ConfigureAwait(false);
                return;
            case "sys":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendReportAsync(chatId, () => _info.GetSystemInfoAsync(ct), ct).ConfigureAwait(false);
                return;
            case "disk":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendReportAsync(chatId, () => Task.FromResult(_info.GetDisks()), ct).ConfigureAwait(false);
                return;
            case "bat":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendReportAsync(chatId, () => Task.FromResult(_info.GetBattery()), ct).ConfigureAwait(false);
                return;
            case "net":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendReportAsync(chatId, () => _info.GetNetworkAsync(ct), ct).ConfigureAwait(false);
                return;
            case "ps":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendReportAsync(chatId, () => Task.FromResult(_info.GetTopProcesses()), ct).ConfigureAwait(false);
                return;
            case "clip":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendClipboardAsync(chatId, ct).ConfigureAwait(false);
                return;
        }

        if (value.StartsWith("ss", StringComparison.Ordinal) && int.TryParse(value[2..], out var index))
        {
            await _telegram.AnswerCallbackAsync(callbackId, Strings.Get("bot.capture.working"), false, ct).ConfigureAwait(false);
            await SendScreenshotAsync(chatId, index.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            string result;
            switch (value)
            {
                case "lock": result = _system.Lock(); break;
                case "sleep": result = _system.Sleep(); break;
                case "mon": result = _system.MonitorOff(); break;
                case "vup": result = _system.VolumeUp(); break;
                case "vdn": result = _system.VolumeDown(); break;
                case "mute": result = _system.ToggleMute(); break;
                case "play": result = _system.MediaPlayPause(); break;
                case "next": result = _system.MediaNext(); break;
                case "prev": result = _system.MediaPrevious(); break;
                case "abort": result = await _system.CancelPendingAsync().ConfigureAwait(false); break;
                default: result = Strings.Get("bot.nothing"); break;
            }
            await _telegram.AnswerCallbackAsync(callbackId, result, false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _telegram.AnswerCallbackAsync(callbackId, ex.Message, true, ct).ConfigureAwait(false);
        }
    }

    private async Task SwitchLanguageAsync(string callbackId, long chatId, long messageId, string tag, CancellationToken ct)
    {
        var language = AppLanguageExtensions.Parse(tag);
        var settings = _settings.Current.Clone();
        settings.Language = language.Tag();
        _settings.Save(settings);
        Strings.Use(language);
        LanguageChanged?.Invoke(language);

        await _telegram.AnswerCallbackAsync(callbackId, Strings.Get("bot.language.changed"), false, ct).ConfigureAwait(false);
        // The pinned shortcut bar is part of the language too, so it is reinstalled.
        await _telegram.SendWithMarkupAsync(chatId, Strings.Get("bot.language.changed"), BotMenu.ShortcutBar(), ct)
            .ConfigureAwait(false);
        await ShowScreenAsync(chatId, messageId, "home", ct).ConfigureAwait(false);
    }

    // ============================================================
    // Settings
    // ============================================================

    /// <summary>
    /// Answers the tap and returns false when the desktop app has made settings
    /// read-only from Telegram. Called on every write path — including the ones that
    /// only became reachable through a panel rendered while writing was still
    /// allowed, since a button in a chat outlives the state it was drawn from.
    /// </summary>
    private async Task<bool> AllowSettingsWriteAsync(string callbackId, CancellationToken ct)
    {
        if (CanWriteSettings)
            return true;
        await _telegram.AnswerCallbackAsync(callbackId, Strings.Get("bot.set.readonly.toast"), true, ct)
            .ConfigureAwait(false);
        return false;
    }

    /// <summary>The same for the message path: the refusal to await, or null when writing is allowed.</summary>
    private Task? RequireSettingsWrite(long chatId, CancellationToken ct)
        => CanWriteSettings ? null : SendTextAsync(chatId, Strings.Get("bot.set.readonly"), ct);

    /// <summary>
    /// Clone, mutate, save. Throws when the write did not reach disk so the caller's
    /// catch reports it: a toggle that only moved on screen is a lie, and the next
    /// render would quietly put it back.
    /// </summary>
    private string ApplySetting(Action<AppSettings> mutate, string resultKey, params object?[] args)
    {
        // Cloned because the poll thread reads the live instance.
        var settings = _settings.Current.Clone();
        mutate(settings);
        if (!_settings.Save(settings))
            throw new InvalidOperationException(Strings.Get("bot.set.savefailed"));
        return args.Length == 0 ? Strings.Get(resultKey) : Strings.Format(resultKey, args);
    }

    private async Task RunSettingsActionAsync(
        string callbackId, long chatId, long messageId, string value, CancellationToken ct)
    {
        if (!await AllowSettingsWriteAsync(callbackId, ct).ConfigureAwait(false))
            return;

        var (op, arg) = SplitOn(value, '.');
        string result;
        string screen;
        try
        {
            switch (op)
            {
                case "t":
                    (result, screen) = ToggleSetting(arg);
                    break;
                case "dlf":
                    result = ApplySetting(s => s.DownloadFolder = string.Empty, "act.set.folderdefault");
                    screen = "sbot";
                    break;
                case "pln":
                    result = await SetPowerPlanAsync(arg, ct).ConfigureAwait(false);
                    screen = "spln";
                    break;
                case "bri":
                    result = SetBrightness(arg);
                    screen = "sbri";
                    break;
                case "bt":
                    result = await SetBluetoothAsync(arg == "1", ct).ConfigureAwait(false);
                    screen = "sblu";
                    break;
                case "wfc":
                    result = await ConnectWifiAsync(chatId, arg, ct).ConfigureAwait(false);
                    screen = "swif";
                    break;
                case "erm":
                    (result, screen) = ClearOneEmoji(arg);
                    break;
                default:
                    await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                    return;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _telegram.AnswerCallbackAsync(callbackId, ex.Message, true, ct).ConfigureAwait(false);
            return;
        }

        await _telegram.AnswerCallbackAsync(callbackId, result, false, ct).ConfigureAwait(false);
        // Re-rendered so the marks match what was just saved. Tapping the state a
        // screen is already in is not an error: EditMessageAsync absorbs Telegram's
        // "message is not modified" rather than sending a duplicate panel.
        await ShowScreenAsync(chatId, messageId, screen, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The low-risk booleans. The payload carries the state to set, not "the other
    /// one", so a stale panel tapped twice lands where its caption promised.
    /// </summary>
    private (string Result, string Screen) ToggleSetting(string arg)
    {
        var (key, state) = SplitOn(arg, '.');
        var on = state == "1";
        return key switch
        {
            "swin" => (SetStartWithWindows(on), "sst"),
            "asb" => (ApplySetting(s => s.AutoStartBot = on, on ? "act.set.on" : "act.set.off",
                          Strings.Get("bot.set.startup.autobot")), "sst"),
            "smin" => (ApplySetting(s => s.StartMinimized = on, on ? "act.set.on" : "act.set.off",
                           Strings.Get("bot.set.startup.startmin")), "sst"),
            "noti" => (ApplySetting(s => s.NotifyOnStartup = on, on ? "act.set.on" : "act.set.off",
                           Strings.Get("bot.set.startup.notify")), "sst"),

            "pemj" => (ApplySetting(s => s.UsePremiumEmoji = on, on ? "act.set.on" : "act.set.off",
                           Strings.Get("bot.set.emoji.use")), "semj"),

            // The two update switches are coupled the same way the desktop couples
            // them: nothing may install unattended while nothing is checking, because
            // a screen showing both on would be saying something untrue.
            "auc" => (ApplySetting(s =>
                      {
                          s.AutoCheckUpdates = on;
                          if (!on) s.AutoInstallUpdates = false;
                      }, on ? "act.set.on" : "act.set.off", Strings.Get("bot.set.pref.autocheck")), "sbot"),
            "aui" => (ApplySetting(s =>
                      {
                          s.AutoInstallUpdates = on;
                          if (on) s.AutoCheckUpdates = true;
                      }, on ? "act.set.on" : "act.set.off", Strings.Get("bot.set.pref.autoinstall")), "sbot"),

            _ => (Strings.Get("bot.nothing"), "set"),
        };
    }

    /// <summary>
    /// Start-with-Windows is a registry entry as well as a stored flag. Both have to
    /// agree, so a save that fails puts the registry back rather than leaving the
    /// machine launching an app whose settings say it should not.
    /// </summary>
    private string SetStartWithWindows(bool on)
    {
        if (_startup is null)
            throw new InvalidOperationException(Strings.Get("bot.set.startup.unmanaged"));

        _startup.SetEnabled(on);
        try
        {
            return ApplySetting(s => s.StartWithWindows = on, on ? "act.set.on" : "act.set.off",
                Strings.Get("bot.set.startup.startwin"));
        }
        catch
        {
            _startup.SetEnabled(!on);
            throw;
        }
    }

    private async Task<string> SetPowerPlanAsync(string id, CancellationToken ct)
    {
        RequirePcSettings();
        // Validated before it reaches a command line: this value came off a button
        // payload, and a power plan is a GUID or it is nothing.
        if (!Guid.TryParse(id, out _))
            throw new InvalidOperationException(Strings.Get("act.plan.unknown"));
        return await _pc!.SetPowerPlanAsync(id, ct).ConfigureAwait(false);
    }

    private string SetBrightness(string arg)
    {
        RequirePcSettings();
        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
            throw new InvalidOperationException(Strings.Get("bot.prompt.notanumber"));
        return _pc!.SetBrightness(percent);
    }

    private async Task<string> SetBluetoothAsync(bool on, CancellationToken ct)
    {
        RequirePcSettings();
        return await _pc!.SetBluetoothAsync(on, ct).ConfigureAwait(false);
    }

    private async Task<string> ConnectWifiAsync(long chatId, string arg, CancellationToken ct)
    {
        RequirePcSettings();
        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            throw new InvalidOperationException(Strings.Get("bot.set.stale"));

        // The profile name itself is too long for a payload, so the button carried an
        // index into the list this chat was last shown. A panel that outlived the list
        // says so rather than connecting to whatever now sits at that position.
        var profile = _wifiList.Get(chatId, index)
                      ?? throw new InvalidOperationException(Strings.Get("bot.set.stale"));
        return await _pc!.ConnectWifiAsync(profile, ct).ConfigureAwait(false);
    }

    private void RequirePcSettings()
    {
        if (_pc is null)
            throw new InvalidOperationException(Strings.Get("bot.set.win.unavailable"));
    }

    /// <summary>The confirmed half of a settings change: permissions, revoke, Wi-Fi off.</summary>
    private async Task RunConfirmedSettingAsync(
        string callbackId, long chatId, long messageId, string action, CancellationToken ct)
    {
        string result;
        string screen;
        try
        {
            if (action.StartsWith("p.", StringComparison.Ordinal))
            {
                var parts = action.Split('.');
                var on = parts.Length > 2 && parts[2] == "1";
                result = parts[1] switch
                {
                    "shell" => ApplySetting(s => s.AllowShellCommands = on, on ? "act.set.on" : "act.set.off",
                                   Strings.Get("bot.set.perm.shell")),
                    "file" => ApplySetting(s => s.AllowFileAccess = on, on ? "act.set.on" : "act.set.off",
                                  Strings.Get("bot.set.perm.files")),
                    "inp" => ApplySetting(s => s.AllowInputInjection = on, on ? "act.set.on" : "act.set.off",
                                 Strings.Get("bot.set.perm.typing")),
                    _ => Strings.Get("bot.nothing"),
                };
                screen = "sper";
            }
            else if (action.StartsWith("cr.", StringComparison.Ordinal))
            {
                result = RevokeChat(action[3..]);
                screen = "scht";
            }
            else if (action == "ecl")
            {
                result = _emojiImporter.ClearAll();
                screen = "semj";
            }
            else if (action == "wd")
            {
                RequirePcSettings();
                result = await _pc!.DisconnectWifiAsync(ct).ConfigureAwait(false);
                screen = "swif";
            }
            else
            {
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _telegram.AnswerCallbackAsync(callbackId, ex.Message, true, ct).ConfigureAwait(false);
            return;
        }

        await _telegram.AnswerCallbackAsync(callbackId, result, false, ct).ConfigureAwait(false);
        await ShowScreenAsync(chatId, messageId, screen, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes a chat off the whitelist, doing what the desktop's Revoke does: save,
    /// then drop everything the router still holds for it. Revoking the last one is
    /// refused — it would leave the PC with no way in short of the desktop app.
    /// </summary>
    private string RevokeChat(string raw)
    {
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)
            || !_settings.Current.AuthorizedChatIds.Contains(target))
            throw new InvalidOperationException(Strings.Get("bot.set.chat.gone"));
        if (_settings.Current.AuthorizedChatIds.Count <= 1)
            throw new InvalidOperationException(Strings.Get("bot.set.chat.lastone"));

        var name = _settings.Current.NameFor(target);
        // Normalize() drops the orphaned display name on save, so it is not removed here.
        var result = ApplySetting(s => s.AuthorizedChatIds.Remove(target), "bot.set.chat.revoked", name);
        Forget(target);
        _log.Info($"Chat {target} revoked from Telegram.");
        return result;
    }

    private string RenameChat(string raw, string name)
    {
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)
            || !_settings.Current.AuthorizedChatIds.Contains(target))
            throw new InvalidOperationException(Strings.Get("bot.set.chat.gone"));

        var trimmed = TextUtil.Clip(name.Trim(), 48);
        if (trimmed.Length == 0)
            throw new InvalidOperationException(Strings.Get("bot.set.chat.badname"));

        var key = target.ToString(CultureInfo.InvariantCulture);
        return ApplySetting(s => s.ChatNames[key] = trimmed, "bot.set.chat.renamed", trimmed);
    }

    // ============================================================
    // Premium emoji
    //
    // The rules live in EmojiImporter, which the desktop window shares. Only the
    // parts that are about *this* surface stay here: turning a button payload back
    // into the emoji it pointed at, and keeping that payload short.
    // ============================================================

    /// <summary>Takes one emoji's premium stand-in away again.</summary>
    private (string Result, string Screen) ClearOneEmoji(string arg)
    {
        var emoji = EmojiAt(arg) ?? throw new InvalidOperationException(Strings.Get("bot.set.stale"));
        return (_emojiImporter.ClearOne(emoji), PageOf(arg));
    }

    /// <summary>
    /// The list screen holding a given emoji. Undoing one on page five should leave
    /// you on page five; the payload already says which emoji it was, so there is
    /// nothing to look up.
    /// </summary>
    private static string PageOf(string? index)
    {
        var i = index is not null
                && int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(parsed, 0)
            : 0;
        return "seml." + (i / BotMenu.EmojiPageSize).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The catalogue emoji a payload's index refers to, or null when it is stale.
    ///
    /// The payload carries an index rather than the emoji itself because the
    /// catalogue is built the same way on every run, and an emoji can be four bytes
    /// of a sixty-four byte budget that also has to hold the prefix.
    /// </summary>
    private static string? EmojiAt(string? index)
    {
        if (index is null
            || !int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            || i < 0 || i >= EmojiCatalog.All.Count)
            return null;
        return EmojiCatalog.All[i].Emoji;
    }

    /// <summary>Splits "a.b.c" into ("a", "b.c"), or ("a", "") when there is no separator.</summary>
    private static (string Head, string Tail) SplitOn(string value, char separator)
    {
        var idx = value.IndexOf(separator);
        return idx < 0 ? (value, string.Empty) : (value[..idx], value[(idx + 1)..]);
    }

    // ============================================================
    // Files
    // ============================================================

    private async Task OpenListingEntryAsync(string callbackId, long chatId, long messageId, string value, CancellationToken ct)
    {
        await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
        if (!_settings.Current.AllowFileAccess)
        {
            await SendTextAsync(chatId, Strings.Get("bot.file.off"), ct).ConfigureAwait(false);
            return;
        }

        var listing = _files.CurrentFor(chatId);
        if (listing is null)
        {
            await SendTextAsync(chatId, Strings.Get("bot.files.stale"), ct).ConfigureAwait(false);
            return;
        }

        if (value == "up")
        {
            if (listing.Parent is { Length: > 0 } parent)
                await ShowFolderAsync(chatId, messageId, parent, ct).ConfigureAwait(false);
            return;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            || index < 0 || index >= listing.Entries.Count)
        {
            await SendTextAsync(chatId, Strings.Get("bot.files.stale"), ct).ConfigureAwait(false);
            return;
        }

        var entry = listing.Entries[index];
        if (entry.IsDirectory)
            await ShowFolderAsync(chatId, messageId, entry.FullPath, ct).ConfigureAwait(false);
        else
            await SendFileAsync(chatId, entry.FullPath, ct).ConfigureAwait(false);
    }

    private async Task ShowFolderAsync(long chatId, long messageId, string path, CancellationToken ct)
    {
        try
        {
            var listing = _files.List(chatId, path);
            await RenderAsync(chatId, messageId, ListingScreen(listing), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, Strings.Format("bot.err", TextUtil.Html(ex.Message)), ct).ConfigureAwait(false);
        }
    }

    private static BotMenu.Screen ListingScreen(FolderListing listing)
    {
        var rows = new List<List<TgInlineKeyboardButton>>();
        for (var i = 0; i < listing.Entries.Count; i++)
        {
            var entry = listing.Entries[i];
            var label = entry.IsDirectory
                ? Strings.Format("bot.files.folder", TextUtil.Clip(entry.Name, 28))
                : Strings.Format("bot.files.file", TextUtil.Clip(entry.Name, 22)) + $"  {TextUtil.HumanBytes(entry.Size)}";
            rows.Add(new List<TgInlineKeyboardButton> { new(label, $"f:{i}") });
        }

        var footer = new List<TgInlineKeyboardButton>();
        if (listing.Parent is { Length: > 0 })
            footer.Add(new TgInlineKeyboardButton(Strings.Get("bot.files.up"), "f:up"));
        footer.Add(new TgInlineKeyboardButton(Strings.Get("bot.menu.back"), "m:home"));
        rows.Add(footer);

        return new BotMenu.Screen(FileBrowser.Describe(listing),
            new TgInlineKeyboardMarkup { InlineKeyboard = rows });
    }

    private async Task SendFileAsync(long chatId, string path, CancellationToken ct)
    {
        try
        {
            await _telegram.SendChatActionAsync(chatId, "upload_document", ct).ConfigureAwait(false);
            var bytes = _files.Read(path, TelegramClient.MaxUploadBytes);
            var name = System.IO.Path.GetFileName(path);
            await _telegram.SendDocumentAsync(chatId, bytes, name,
                Strings.Format("bot.files.sent", TextUtil.Html(name)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, Strings.Format("bot.err", TextUtil.Html(ex.Message)), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Takes delivery of a document sent to the bot and writes it to the download folder.</summary>
    private async Task ReceiveDocumentAsync(long chatId, TgDocument document, CancellationToken ct)
    {
        if (!_settings.Current.AllowFileAccess)
        {
            await SendTextAsync(chatId, Strings.Get("bot.file.off"), ct).ConfigureAwait(false);
            return;
        }

        Count("file:receive");
        try
        {
            if (document.FileSize is { } size && size > TelegramClient.MaxDownloadBytes)
            {
                await SendTextAsync(chatId, Strings.Format("bot.files.toobig",
                    TextUtil.HumanBytes(size), TextUtil.HumanBytes(TelegramClient.MaxDownloadBytes)), ct).ConfigureAwait(false);
                return;
            }

            await SendTextAsync(chatId, Strings.Get("bot.files.receiving"), ct).ConfigureAwait(false);
            var file = await _telegram.GetFileAsync(document.FileId, ct).ConfigureAwait(false);
            var bytes = await _telegram.DownloadFileAsync(file.FilePath!, TelegramClient.MaxDownloadBytes, ct).ConfigureAwait(false);
            var saved = _files.Save(document.FileName, bytes, FileBrowser.DefaultDownloadFolder(_settings.Current));
            await SendTextAsync(chatId, Strings.Format("bot.files.saved", TextUtil.Html(saved)), ct).ConfigureAwait(false);
            _log.Info($"Received '{document.FileName}' from chat {chatId} into {saved}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, Strings.Format("bot.err", TextUtil.Html(ex.Message)), ct).ConfigureAwait(false);
        }
    }

    // ============================================================
    // Messages
    // ============================================================

    private async Task HandleMessageAsync(TgMessage msg, CancellationToken ct)
    {
        var chatId = msg.Chat?.Id ?? 0;
        if (chatId == 0)
            return;

        if (msg.Text is not { Length: > 0 })
        {
            // A document, photo or voice note is still a message the user meant something by.
            if (IsAuthorized(chatId) && _commandLimit.Allow(chatId) && msg.Document is { } document)
                await ReceiveDocumentAsync(chatId, document, ct).ConfigureAwait(false);
            else if (IsAuthorized(chatId) && _commandLimit.Allow(chatId))
                await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
            return;
        }

        var raw = msg.Text.Trim();
        var (cmd, arg) = ParseCommand(raw);

        if (!IsAuthorized(chatId))
        {
            await HandleUnauthorizedAsync(msg, chatId, cmd, arg, ct).ConfigureAwait(false);
            return;
        }
        if (!_commandLimit.Allow(chatId))
        {
            await SendTextAsync(chatId, Strings.Get("bot.ratelimited"), ct).ConfigureAwait(false);
            return;
        }

        // Shortcut-bar taps arrive as ordinary text and must be recognised BEFORE a
        // pending prompt claims them — otherwise tapping "Lock" while a "Type text…"
        // prompt is open types the word "Lock" into the focused window instead.
        // The bare form is only accepted when nothing is waiting for an answer. A tap
        // on a bar wearing premium icons arrives as the plain word "Lock", and so does
        // someone answering "type this into the focused window" with the word Lock —
        // the two are indistinguishable. The prompt wins, because it is an explicit
        // request made seconds ago, and because locking a PC that was asked to type is
        // far worse than typing a word that was meant as a tap.
        if (ShortcutActionFor(raw, allowBareCaption: !_prompts.HasPending(chatId)) is { } shortcut)
        {
            _prompts.Clear(chatId);
            switch (shortcut)
            {
                case "menu":
                    await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
                    return;
                case "shot":
                    Count("shortcut:screenshot");
                    await SendScreenshotAsync(chatId, null, ct).ConfigureAwait(false);
                    return;
                case "lock":
                    Count("shortcut:lock");
                    await SendResultAsync(chatId, () => _system.Lock(), ct).ConfigureAwait(false);
                    return;
                case "power":
                    await ShowScreenAsync(chatId, 0, "pwr", ct).ConfigureAwait(false);
                    return;
            }
        }

        // A pending prompt claims the next plain message.
        if (!raw.StartsWith('/') && _prompts.HasPending(chatId))
        {
            var kind = _prompts.Take(chatId, out var promptArg);
            if (kind != PromptKind.None)
            {
                // The entities travel with the answer because a premium emoji is not in
                // the text: what arrives is the ordinary emoji it stands for, plus an
                // entity naming the custom one. The text alone cannot tell them apart.
                await FulfilPromptAsync(chatId, kind, raw, promptArg, msg.Entities, ct).ConfigureAwait(false);
                return;
            }
        }

        if (!raw.StartsWith('/'))
        {
            // Anything else opens the panel rather than being ignored.
            await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
            return;
        }

        Count("/" + cmd);
        _log.Info($"Command '{cmd}' from chat {chatId}.");
        await RunTypedCommandAsync(chatId, cmd, arg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches a shortcut-bar caption in <em>any</em> language. A user who switches
    /// language still has the previous bar pinned in Telegram until a new one is sent,
    /// so both sets have to keep working.
    /// </summary>
    /// <param name="allowBareCaption">
    /// Whether a caption stripped of its emoji may be matched. A bar wearing premium
    /// emoji carries them in the buttons' icon field rather than in the label, so a
    /// tap comes back as "Lock" rather than "🔒 Lock" — but that is also just a word
    /// somebody might type, so the caller decides when it is safe to read as a tap.
    /// </param>
    private string? ShortcutActionFor(string text, bool allowBareCaption)
    {
        foreach (var (caption, action) in BotMenu.ShortcutCaptions())
        {
            // The full caption is unambiguous: nobody types "🔒 Lock" by hand.
            if (string.Equals(caption, text, StringComparison.Ordinal))
                return action;

            if (!allowBareCaption)
                continue;

            var (emoji, label) = EmojiText.SplitLeadingEmoji(caption);
            if (emoji.Length > 0 && IsMovedToIcon(emoji)
                && string.Equals(label, text, StringComparison.Ordinal))
                return action;
        }
        return null;
    }

    /// <summary>
    /// Whether this emoji is currently being lifted off button labels — the same
    /// three conditions the styler applies before it rewrites a keyboard.
    /// </summary>
    private bool IsMovedToIcon(string emoji) =>
        _emoji is { State: PremiumEmojiState.Working }
        && _settings.Current.UsePremiumEmoji
        && _settings.Current.PremiumEmoji.ContainsKey(emoji);

    private async Task FulfilPromptAsync(
        long chatId, PromptKind kind, string input, string? arg,
        IReadOnlyList<TgMessageEntity>? entities, CancellationToken ct)
    {
        Count("prompt:" + kind);

        // A prompt stays open for three minutes, which is long enough for the desktop
        // to switch remote settings off while one is waiting for an answer.
        if (ChatPrompts.IsSettingsPrompt(kind) && RequireSettingsWrite(chatId, ct) is { } refusedSetting)
        {
            await refusedSetting.ConfigureAwait(false);
            return;
        }

        switch (kind)
        {
            case PromptKind.PollTimeout:
                // Normalize() clamps to 5-50 on save, so the value is handed over raw
                // rather than clamped twice in two places that could disagree.
                await SendResultAsync(chatId, () =>
                {
                    if (!int.TryParse(input.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                        throw new InvalidOperationException(Strings.Get("bot.prompt.notawholenumber"));
                    var applied = Math.Clamp(seconds, 5, 50);
                    return ApplySetting(s => s.PollTimeoutSeconds = applied, "act.set.poll", applied);
                }, ct).ConfigureAwait(false);
                return;

            case PromptKind.LogRetention:
                await SendResultAsync(chatId, () =>
                {
                    if (!int.TryParse(input.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                        throw new InvalidOperationException(Strings.Get("bot.prompt.notawholenumber"));
                    var applied = Math.Clamp(days, 0, 365);
                    return applied == 0
                        ? ApplySetting(s => s.LogRetentionDays = 0, "act.set.logforever")
                        : ApplySetting(s => s.LogRetentionDays = applied, "act.set.logdays", applied);
                }, ct).ConfigureAwait(false);
                return;

            case PromptKind.DownloadFolder:
                await SendResultAsync(chatId, () =>
                {
                    var path = input.Trim();
                    // Checked before it is stored: a folder that is not there would only
                    // fail later, on the first file someone sent, with nothing to explain it.
                    if (!System.IO.Directory.Exists(path))
                        throw new InvalidOperationException(Strings.Get("act.set.foldermissing"));
                    // Not escaped here: SendResultAsync escapes whatever it is handed.
                    return ApplySetting(s => s.DownloadFolder = path, "act.set.folder", path);
                }, ct).ConfigureAwait(false);
                return;

            case PromptKind.Brightness:
                await SendResultAsync(chatId, () =>
                {
                    RequirePcSettings();
                    if (!int.TryParse(input.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                        throw new InvalidOperationException(Strings.Get("bot.prompt.notanumber"));
                    return _pc!.SetBrightness(percent);
                }, ct).ConfigureAwait(false);
                return;

            case PromptKind.RenameChat:
                await SendResultAsync(chatId, () => RenameChat(arg ?? string.Empty, input), ct)
                    .ConfigureAwait(false);
                return;

            case PromptKind.EmojiPack:
                await SendAsyncResultAsync(chatId,
                    () => _emojiImporter.ImportPackAsync(input, entities, ct), ct).ConfigureAwait(false);
                await ShowScreenAsync(chatId, 0, "semj", ct).ConfigureAwait(false);
                return;

            case PromptKind.PremiumEmoji:
                await SendAsyncResultAsync(chatId,
                    () => _emojiImporter.AdoptAsync(entities, null, null, ct), ct).ConfigureAwait(false);
                await ShowScreenAsync(chatId, 0, "semj", ct).ConfigureAwait(false);
                return;

            case PromptKind.PremiumEmojiFor:
                await SendAsyncResultAsync(chatId, () =>
                {
                    // A panel can sit in a chat for hours. If its index no longer names
                    // an emoji, the tap has to be refused rather than falling back to
                    // "convert whatever was sent" — which would silently convert an
                    // emoji the user never pointed at and report it as a success.
                    var target = EmojiAt(arg)
                        ?? throw new InvalidOperationException(Strings.Get("bot.set.stale"));
                    return _emojiImporter.AdoptAsync(entities, null, target, ct);
                }, ct).ConfigureAwait(false);
                await ShowScreenAsync(chatId, 0, PageOf(arg), ct).ConfigureAwait(false);
                return;
        }

        switch (kind)
        {
            case PromptKind.Volume:
                if (int.TryParse(input.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
                    await SendResultAsync(chatId, () => _system.SetVolume(level), ct).ConfigureAwait(false);
                else
                    await SendTextAsync(chatId, Strings.Get("bot.prompt.notanumber"), ct).ConfigureAwait(false);
                return;
            case PromptKind.KillProcess:
                await SendResultAsync(chatId, () => _system.KillProcess(input), ct).ConfigureAwait(false);
                return;
            case PromptKind.Clipboard:
                await SendResultAsync(chatId, () => _system.SetClipboardText(input), ct).ConfigureAwait(false);
                return;
            case PromptKind.TypeText:
                if (RequireTyping(chatId, ct) is { } refusedType)
                {
                    await refusedType.ConfigureAwait(false);
                    return;
                }
                await SendResultAsync(chatId, () => _system.TypeText(input), ct).ConfigureAwait(false);
                return;
            case PromptKind.OpenLink:
                await OpenTargetAsync(chatId, input, ct).ConfigureAwait(false);
                return;
            case PromptKind.Speak:
                await SendAsyncResultAsync(chatId, () => _system.SpeakAsync(input, ct), ct).ConfigureAwait(false);
                return;
            case PromptKind.ShellCommand:
                await RunShellAsync(chatId, input, ct).ConfigureAwait(false);
                return;
            case PromptKind.Path:
                if (RequireFileAccess(chatId, ct) is { } refusedPath)
                {
                    await refusedPath.ConfigureAwait(false);
                    return;
                }
                await ShowFolderAsync(chatId, 0, input, ct).ConfigureAwait(false);
                return;
            case PromptKind.GetFile:
                if (RequireFileAccess(chatId, ct) is { } refusedGet)
                {
                    await refusedGet.ConfigureAwait(false);
                    return;
                }
                await SendFileAsync(chatId, input, ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Returns the refusal to await when file access is off, or null when it is on.</summary>
    private Task? RequireFileAccess(long chatId, CancellationToken ct)
        => _settings.Current.AllowFileAccess
            ? null
            : SendTextAsync(chatId, Strings.Get("bot.file.off"), ct);

    /// <summary>The same, for typing into the focused window.</summary>
    private Task? RequireTyping(long chatId, CancellationToken ct)
        => _settings.Current.AllowInputInjection
            ? null
            : SendTextAsync(chatId, Strings.Get("bot.type.off"), ct);

    private async Task RunTypedCommandAsync(long chatId, string cmd, string? arg, CancellationToken ct)
    {
        switch (cmd)
        {
            case "start":
                await SendWelcomeAsync(chatId, ct).ConfigureAwait(false);
                break;
            case "menu":
            case "help":
                await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
                break;
            case "screenshot":
            case "ss":
                await SendScreenshotAsync(chatId, arg, ct).ConfigureAwait(false);
                break;
            case "sysinfo":
            case "info":
                await SendReportAsync(chatId, () => _info.GetSystemInfoAsync(ct), ct).ConfigureAwait(false);
                break;
            case "disks":
                await SendReportAsync(chatId, () => Task.FromResult(_info.GetDisks()), ct).ConfigureAwait(false);
                break;
            case "battery":
                await SendReportAsync(chatId, () => Task.FromResult(_info.GetBattery()), ct).ConfigureAwait(false);
                break;
            case "processes":
            case "ps":
                await SendReportAsync(chatId, () => Task.FromResult(_info.GetTopProcesses()), ct).ConfigureAwait(false);
                break;
            case "network":
            case "net":
                await SendReportAsync(chatId, () => _info.GetNetworkAsync(ct), ct).ConfigureAwait(false);
                break;
            case "lock":
                await SendResultAsync(chatId, () => _system.Lock(), ct).ConfigureAwait(false);
                break;
            case "sleep":
                await SendResultAsync(chatId, () => _system.Sleep(), ct).ConfigureAwait(false);
                break;
            case "cancel":
                await SendAsyncResultAsync(chatId, () => _system.CancelPendingAsync(), ct).ConfigureAwait(false);
                break;
            case "volume":
            case "vol":
                if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct))
                    await SendResultAsync(chatId, () => _system.SetVolume(pct), ct).ConfigureAwait(false);
                else
                    await ShowScreenAsync(chatId, 0, "aud", ct).ConfigureAwait(false);
                break;
            case "mute":
                await SendResultAsync(chatId, () => _system.ToggleMute(), ct).ConfigureAwait(false);
                break;
            case "kill":
                if (string.IsNullOrWhiteSpace(arg))
                    await ShowScreenAsync(chatId, 0, "prc", ct).ConfigureAwait(false);
                else
                    await SendResultAsync(chatId, () => _system.KillProcess(arg!), ct).ConfigureAwait(false);
                break;
            case "clipboard":
                await SendClipboardAsync(chatId, ct).ConfigureAwait(false);
                break;
            // Each of these needs a value. Without one, ask for it rather than going silent.
            case "clip":
                if (!string.IsNullOrWhiteSpace(arg))
                    await SendResultAsync(chatId, () => _system.SetClipboardText(arg!), ct).ConfigureAwait(false);
                else
                    await AskInChatAsync(chatId, PromptKind.Clipboard, ct).ConfigureAwait(false);
                break;
            case "open":
                if (!string.IsNullOrWhiteSpace(arg))
                    await OpenTargetAsync(chatId, arg!, ct).ConfigureAwait(false);
                else
                    await AskInChatAsync(chatId, PromptKind.OpenLink, ct).ConfigureAwait(false);
                break;
            case "type":
                if (!_settings.Current.AllowInputInjection)
                    await SendTextAsync(chatId, Strings.Get("bot.type.off"), ct).ConfigureAwait(false);
                else if (!string.IsNullOrWhiteSpace(arg))
                    await SendResultAsync(chatId, () => _system.TypeText(arg!), ct).ConfigureAwait(false);
                else
                    await AskInChatAsync(chatId, PromptKind.TypeText, ct).ConfigureAwait(false);
                break;
            case "say":
                if (!string.IsNullOrWhiteSpace(arg))
                    await SendAsyncResultAsync(chatId, () => _system.SpeakAsync(arg!, ct), ct).ConfigureAwait(false);
                else
                    await AskInChatAsync(chatId, PromptKind.Speak, ct).ConfigureAwait(false);
                break;
            case "cmd":
                // Called unconditionally so the "switched off" notice is still sent.
                if (!_settings.Current.AllowShellCommands || !string.IsNullOrWhiteSpace(arg))
                    await RunShellAsync(chatId, arg ?? string.Empty, ct).ConfigureAwait(false);
                else
                    await AskInChatAsync(chatId, PromptKind.ShellCommand, ct).ConfigureAwait(false);
                break;
            case "files":
            case "ls":
                if (!_settings.Current.AllowFileAccess)
                    await SendTextAsync(chatId, Strings.Get("bot.file.off"), ct).ConfigureAwait(false);
                else if (!string.IsNullOrWhiteSpace(arg))
                    await ShowFolderAsync(chatId, 0, arg!, ct).ConfigureAwait(false);
                else
                    await AskInChatAsync(chatId, PromptKind.Path, ct).ConfigureAwait(false);
                break;
            case "get":
                if (!_settings.Current.AllowFileAccess)
                    await SendTextAsync(chatId, Strings.Get("bot.file.off"), ct).ConfigureAwait(false);
                else if (!string.IsNullOrWhiteSpace(arg))
                    await SendFileAsync(chatId, arg!, ct).ConfigureAwait(false);
                else
                    await AskInChatAsync(chatId, PromptKind.GetFile, ct).ConfigureAwait(false);
                break;
            case "lang":
            case "language":
                await SetLanguageFromCommandAsync(chatId, arg, ct).ConfigureAwait(false);
                break;
            case "settings":
            case "set":
                // Never guarded: reading how your own PC is configured is not a write,
                // and the read-only panel is what explains why nothing is tappable.
                await ShowScreenAsync(chatId, 0, "set", ct).ConfigureAwait(false);
                break;

            case "emoji":
                await ShowScreenAsync(chatId, 0, "semj", ct).ConfigureAwait(false);
                break;
            case "whoami":
                await SendTextAsync(chatId, Strings.Format("bot.chatid", chatId), ct).ConfigureAwait(false);
                break;
            case "ping":
                await SendTextAsync(chatId, Strings.Get("bot.pong"), ct).ConfigureAwait(false);
                break;
            default:
                await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task SetLanguageFromCommandAsync(long chatId, string? arg, CancellationToken ct)
    {
        var language = string.IsNullOrWhiteSpace(arg) ? BotMenu.Other() : AppLanguageExtensions.Parse(arg);
        var settings = _settings.Current.Clone();
        settings.Language = language.Tag();
        _settings.Save(settings);
        Strings.Use(language);
        LanguageChanged?.Invoke(language);
        await _telegram.SendWithMarkupAsync(chatId, Strings.Get("bot.language.changed"), BotMenu.ShortcutBar(), ct)
            .ConfigureAwait(false);
        await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
    }

    // ============================================================
    // Actions that produce output
    // ============================================================

    private async Task SendWelcomeAsync(long chatId, CancellationToken ct)
    {
        await _telegram.SendWithMarkupAsync(chatId,
            Strings.Format("bot.welcome", TextUtil.Html(Environment.MachineName)),
            BotMenu.ShortcutBar(), ct).ConfigureAwait(false);
        await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
    }

    private async Task SendScreenshotAsync(long chatId, string? arg, CancellationToken ct)
    {
        try
        {
            await _telegram.SendChatActionAsync(chatId, "upload_photo", ct).ConfigureAwait(false);

            ScreenCapture capture;
            string caption;
            var now = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                capture = _screenshot.CaptureScreen(index, TelegramClient.MaxPhotoBytes, MaxPhotoDimensionSum);
                caption = Strings.Format("bot.capture.caption.monitor", index + 1, now);
            }
            else
            {
                capture = _screenshot.CaptureAll(TelegramClient.MaxPhotoBytes, MaxPhotoDimensionSum);
                caption = Strings.Format("bot.capture.caption.desktop", now);
            }

            // A capture too large or too wide for sendPhoto still gets through, as a file.
            if (capture.IsPhoto)
                await _telegram.SendPhotoAsync(chatId, capture.Data, capture.FileName, caption, ct).ConfigureAwait(false);
            else
                await _telegram.SendDocumentAsync(chatId, capture.Data, capture.FileName, caption, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, Strings.Format("bot.capture.failed", TextUtil.Html(ex.Message)), ct).ConfigureAwait(false);
        }
    }

    /// <summary>sendPhoto rejects an image whose width plus height exceeds this.</summary>
    public const int MaxPhotoDimensionSum = 10000;

    private async Task SendClipboardAsync(long chatId, CancellationToken ct)
    {
        try
        {
            var text = _system.GetClipboardText();
            var body = TextUtil.Pre(text);
            if (body.Length <= 3500)
                await _telegram.SendMessageAsync(chatId, body, null, ct).ConfigureAwait(false);
            else
                await _telegram.SendDocumentAsync(chatId, Encoding.UTF8.GetBytes(text),
                    $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.txt", Strings.Get("bot.clipboard.caption"), ct)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, Strings.Format("bot.err", TextUtil.Html(ex.Message)), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opening a web link is harmless, but "open" with a local path runs whatever it
    /// points at — the same power as a shell command. So a web link is always allowed
    /// and anything local needs the file-access switch the desktop app owns.
    /// </summary>
    private async Task OpenTargetAsync(long chatId, string target, CancellationToken ct)
    {
        var trimmed = target.Trim();
        var isWebLink = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        if (!isWebLink && !_settings.Current.AllowFileAccess)
        {
            await SendTextAsync(chatId, Strings.Get("bot.open.weblinksonly"), ct).ConfigureAwait(false);
            return;
        }

        await SendResultAsync(chatId, () => _system.OpenTarget(trimmed), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks for a missing value, offering a way out of the prompt.
    ///
    /// A prompt with no exit stranded the chat until it timed out three minutes later,
    /// and <c>BotMenu.CancelPrompt</c> existed but was never sent — the whole "x"
    /// branch was unreachable. The button goes on the prompt itself rather than a
    /// second message: reply markup is one object per message, and since pairing is
    /// private-chat only, a force-reply's pre-aimed composer buys little compared with
    /// a visible way to back out.
    /// </summary>
    private async Task AskInChatAsync(long chatId, PromptKind kind, CancellationToken ct, string? arg = null)
    {
        _prompts.Ask(chatId, kind, arg);
        var text = ChatPrompts.PromptFor(kind)
                   + $"\n<i>{TextUtil.Html(ChatPrompts.PlaceholderFor(kind))}</i>";
        await _telegram.SendMessageAsync(chatId, text, BotMenu.CancelPrompt(), ct).ConfigureAwait(false);
    }

    private async Task RunShellAsync(long chatId, string command, CancellationToken ct)
    {
        if (!_settings.Current.AllowShellCommands)
        {
            await SendTextAsync(chatId, Strings.Get("bot.shell.off"), ct).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            await AskInChatAsync(chatId, PromptKind.ShellCommand, ct).ConfigureAwait(false);
            return;
        }
        try
        {
            await _telegram.SendChatActionAsync(chatId, "typing", ct).ConfigureAwait(false);
            var output = await _system.RunShellCommandAsync(command, ct).ConfigureAwait(false);
            var body = TextUtil.Pre(output);
            if (body.Length <= 3500)
                await _telegram.SendMessageAsync(chatId, body, null, ct).ConfigureAwait(false);
            else
                await _telegram.SendDocumentAsync(chatId, Encoding.UTF8.GetBytes(output),
                    $"output_{DateTime.Now:yyyyMMdd_HHmmss}.txt", Strings.Get("bot.shell.caption"), ct)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, Strings.Format("bot.err", TextUtil.Html(ex.Message)), ct).ConfigureAwait(false);
        }
    }

    private async Task SendResultAsync(long chatId, Func<string> action, CancellationToken ct)
    {
        string message;
        try { message = Strings.Format("bot.ok", TextUtil.Html(action())); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { message = Strings.Format("bot.err", TextUtil.Html(ex.Message)); }
        await SendTextAsync(chatId, message, ct).ConfigureAwait(false);
    }

    private async Task SendAsyncResultAsync(long chatId, Func<Task<string>> action, CancellationToken ct)
    {
        string message;
        try { message = Strings.Format("bot.ok", TextUtil.Html(await action().ConfigureAwait(false))); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { message = Strings.Format("bot.err", TextUtil.Html(ex.Message)); }
        await SendTextAsync(chatId, message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Produces a report and sends it. The producer runs inside the try: a report that
    /// throws while it is being built (an unreadable drive, a process that exits mid-scan)
    /// has to answer with the error, not with silence.
    /// </summary>
    private async Task SendReportAsync(long chatId, Func<Task<string>> producer, CancellationToken ct)
    {
        string message;
        try { message = await producer().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { message = Strings.Format("bot.err", TextUtil.Html(ex.Message)); }
        await SendTextAsync(chatId, message, ct).ConfigureAwait(false);
    }

    private Task SendTextAsync(long chatId, string text, CancellationToken ct)
        => _telegram.SendMessageAsync(chatId, text, null, ct);

    // ============================================================
    // Pairing and authorization
    // ============================================================

    private async Task HandleUnauthorizedAsync(TgMessage msg, long chatId, string cmd, string? arg, CancellationToken ct)
    {
        // Every reply to a stranger is an outbound relay call. Answer the first few and
        // then go quiet, so a bot whose URL leaks cannot be turned into a message pump.
        if (!_strangerLimit.Allow(chatId))
            return;

        if (cmd != "pair")
        {
            await _telegram.SendMessageAsync(chatId, Strings.Get("bot.unpaired"), null, ct).ConfigureAwait(false);
            return;
        }

        // Pairing a group would authorize every current and future member of it, so
        // the code is only ever accepted in a one-to-one chat.
        if (!IsPrivateChat(msg))
        {
            _log.Warning($"Pairing refused in non-private chat {chatId}.");
            await _telegram.SendMessageAsync(chatId, Strings.Get("bot.pair.privateonly"), null, ct).ConfigureAwait(false);
            return;
        }

        string expected;
        lock (_pairingLock)
        {
            var expired = _pairingIssuedAt != default && _clock.UtcNow - _pairingIssuedAt > PairingCodeLifetime;
            var lockedOut = _failedPairAttempts.TryGetValue(chatId, out var tries) && tries >= MaxPairAttemptsPerChat;
            if (string.IsNullOrEmpty(_pairingCode) || expired || lockedOut)
            {
                _log.Warning($"Pairing attempt from {chatId} rejected (no active code, expired, or too many failures).");
                expected = string.Empty;
            }
            else
            {
                expected = _pairingCode;
            }
        }

        if (expected.Length == 0)
        {
            await _telegram.SendMessageAsync(chatId, Strings.Get("bot.pair.closed"), null, ct).ConfigureAwait(false);
            return;
        }

        var provided = Encoding.UTF8.GetBytes((arg ?? string.Empty).Trim());
        if (CryptographicOperations.FixedTimeEquals(provided, Encoding.UTF8.GetBytes(expected)))
        {
            if (!Authorize(chatId, DisplayNameOf(msg)))
            {
                // The code is deliberately NOT consumed here: the chat is not paired, so
                // burning it would leave the user with neither access nor a usable code.
                await _telegram.SendMessageAsync(chatId, Strings.Get("bot.pair.savefailed"), null, ct).ConfigureAwait(false);
                return;
            }
            lock (_pairingLock)
            {
                _pairingCode = string.Empty; // single use
                _failedPairAttempts.Remove(chatId);
            }
            _log.Info($"Chat {chatId} authorized via pairing.");
            await SendWelcomeAsync(chatId, ct).ConfigureAwait(false);
        }
        else
        {
            int tries;
            lock (_pairingLock)
            {
                _failedPairAttempts.TryGetValue(chatId, out tries);
                tries++;
                _failedPairAttempts[chatId] = tries;
            }
            _log.Warning($"Invalid pairing code from {chatId} ({tries}/{MaxPairAttemptsPerChat}).");
            await _telegram.SendMessageAsync(chatId,
                tries >= MaxPairAttemptsPerChat ? Strings.Get("bot.pair.slowdown") : Strings.Get("bot.pair.wrong"),
                null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Telegram reports a one-to-one chat as type "private"; anything else is shared.</summary>
    private static bool IsPrivateChat(TgMessage msg) =>
        string.Equals(msg.Chat?.Type, "private", StringComparison.Ordinal);

    private bool IsAuthorized(long chatId) => _settings.Current.AuthorizedChatIds.Contains(chatId);

    /// <summary>
    /// Adds a chat to the whitelist. Returns false when the change did not reach disk —
    /// the caller must not report success in that case, because the pairing would be
    /// forgotten on the next restart while the single-use code had already been spent.
    /// </summary>
    private bool Authorize(long chatId, string? displayName)
    {
        if (_settings.Current.AuthorizedChatIds.Contains(chatId))
            return true;
        // Clone before mutating: the poll thread reads the live list.
        var settings = _settings.Current.Clone();
        settings.AuthorizedChatIds.Add(chatId);
        if (!string.IsNullOrWhiteSpace(displayName))
            settings.ChatNames[chatId.ToString(CultureInfo.InvariantCulture)] = displayName!;
        if (!_settings.Save(settings))
            return false;
        ChatAuthorized?.Invoke(chatId);
        return true;
    }

    /// <summary>
    /// A human-readable name for whoever just paired, so the desktop can show
    /// "Sara (@sara_k)" instead of a bare chat id when the owner comes to revoke one.
    /// </summary>
    private static string? DisplayNameOf(TgMessage msg)
    {
        var person = msg.From;
        var name = msg.Chat?.Title
                   ?? person?.FirstName
                   ?? msg.Chat?.FirstName;
        var handle = person?.Username ?? msg.Chat?.Username;
        if (string.IsNullOrWhiteSpace(name))
            return string.IsNullOrWhiteSpace(handle) ? null : "@" + handle;
        return string.IsNullOrWhiteSpace(handle) ? name : $"{name} (@{handle})";
    }

    /// <summary>Drops any per-chat state for a chat the desktop app has just un-paired.</summary>
    public void Forget(long chatId)
    {
        _prompts.Clear(chatId);
        _files.Forget(chatId);
        _wifiList.Forget(chatId);
        _commandLimit.Forget(chatId);
        _strangerLimit.Forget(chatId);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private void Count(string label)
    {
        Interlocked.Increment(ref _commandsHandled);
        CommandHandled?.Invoke(label);
    }

    internal static (string cmd, string? arg) ParseCommand(string raw)
    {
        if (!raw.StartsWith('/'))
            return (raw.ToLowerInvariant(), null);
        var body = raw[1..];
        var spaceIdx = body.IndexOf(' ');
        var cmdPart = spaceIdx < 0 ? body : body[..spaceIdx];
        var arg = spaceIdx < 0 ? null : body[(spaceIdx + 1)..].Trim();
        var at = cmdPart.IndexOf('@');
        if (at >= 0) cmdPart = cmdPart[..at];
        return (cmdPart.ToLowerInvariant(), string.IsNullOrEmpty(arg) ? null : arg);
    }

    internal static (string kind, string value) Split(string data)
    {
        var idx = data.IndexOf(':');
        return idx < 0 ? (data, string.Empty) : (data[..idx], data[(idx + 1)..]);
    }
}
