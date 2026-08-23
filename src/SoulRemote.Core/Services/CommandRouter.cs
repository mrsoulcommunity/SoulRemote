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
    private readonly ChatPrompts _prompts;
    private readonly FileBrowser _files = new();

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

    public CommandRouter(
        ISettingsService settings, ITelegramClient telegram, ISystemControlService system,
        IScreenshotService screenshot, ISystemInfoService info, ILogService log, IClock? clock = null)
    {
        _settings = settings;
        _telegram = telegram;
        _system = system;
        _screenshot = screenshot;
        _info = info;
        _log = log;
        _clock = clock ?? SystemClock.Instance;
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
                var (action, question) = ConfirmationFor(value);
                await _telegram.AnswerCallbackAsync(cb.Id, action, false, ct).ConfigureAwait(false);
                var confirm = BotMenu.Confirm(value, question);
                await RenderAsync(chatId, messageId, confirm, ct).ConfigureAwait(false);
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
        => await RenderAsync(chatId, messageId, ScreenFor(chatId, screen), ct).ConfigureAwait(false);

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
        return screen switch
        {
            "cap" => BotMenu.Capture(SafeScreenCount()),
            "pwr" => BotMenu.Power(),
            "aud" => BotMenu.Audio(),
            "inp" => BotMenu.Input(),
            "sys" => BotMenu.System(),
            "prc" => BotMenu.Processes(settings.AllowShellCommands),
            "fil" => BotMenu.Files(settings.AllowFileAccess),
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

    private static (string toast, string question) ConfirmationFor(string action) => action switch
    {
        "sd" => (Strings.Get("bot.confirm.shutdown.toast"), Strings.Get("bot.confirm.shutdown.question")),
        "rs" => (Strings.Get("bot.confirm.restart.toast"), Strings.Get("bot.confirm.restart.question")),
        "lo" => (Strings.Get("bot.confirm.signout.toast"), Strings.Get("bot.confirm.signout.question")),
        "hb" => (Strings.Get("bot.confirm.hibernate.toast"), Strings.Get("bot.confirm.hibernate.question")),
        _ => (Strings.Get("bot.confirm.generic.toast"), Strings.Get("bot.confirm.generic.question")),
    };

    private async Task RunConfirmedAsync(string callbackId, long chatId, long messageId, string action, CancellationToken ct)
    {
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
            _ => PromptKind.None,
        };
        if (kind == PromptKind.None)
        {
            await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
            return;
        }
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

        await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
        await AskInChatAsync(chatId, kind, ct).ConfigureAwait(false);
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
        if (ShortcutActionFor(raw) is { } shortcut)
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
            var kind = _prompts.Take(chatId);
            if (kind != PromptKind.None)
            {
                await FulfilPromptAsync(chatId, kind, raw, ct).ConfigureAwait(false);
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
    private static string? ShortcutActionFor(string text)
    {
        foreach (var (caption, action) in BotMenu.ShortcutCaptions())
        {
            if (string.Equals(caption, text, StringComparison.Ordinal))
                return action;
        }
        return null;
    }

    private async Task FulfilPromptAsync(long chatId, PromptKind kind, string input, CancellationToken ct)
    {
        Count("prompt:" + kind);
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
                if (!string.IsNullOrWhiteSpace(arg))
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

    /// <summary>Asks for a missing value, offering a way out of the prompt.</summary>
    private async Task AskInChatAsync(long chatId, PromptKind kind, CancellationToken ct)
    {
        _prompts.Ask(chatId, kind);
        await _telegram.SendWithMarkupAsync(chatId, ChatPrompts.PromptFor(kind),
            new TgForceReply { Placeholder = ChatPrompts.PlaceholderFor(kind) }, ct)
            .ConfigureAwait(false);
        // A prompt with no exit strands the chat until it times out, so the way back
        // is offered as its own message — force-reply cannot carry an inline keyboard.
        await _telegram.SendMessageAsync(chatId, Strings.Get("bot.prompt.cancel.hint"),
            BotMenu.CancelPrompt(), ct).ConfigureAwait(false);
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
