using System.Security.Cryptography;
using System.Text;
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
    private const int MaxPairAttempts = 5;

    private readonly ISettingsService _settings;
    private readonly ITelegramClient _telegram;
    private readonly ISystemControlService _system;
    private readonly IScreenshotService _screenshot;
    private readonly ISystemInfoService _info;
    private readonly ILogService _log;
    private readonly ChatPrompts _prompts = new();

    private string _pairingCode = string.Empty;
    private int _failedPairAttempts;
    private int _commandsHandled;

    /// <summary>One-time pairing code shown in the desktop app. Setting it clears the lockout.</summary>
    public string PairingCode
    {
        get => _pairingCode;
        set
        {
            _pairingCode = value ?? string.Empty;
            _failedPairAttempts = 0;
        }
    }

    /// <summary>Raised (on a background thread) when a new chat is authorized via pairing.</summary>
    public event Action<long>? ChatAuthorized;

    /// <summary>Raised (on a background thread) after each accepted action, with its name.</summary>
    public event Action<string>? CommandHandled;

    public int CommandsHandled => _commandsHandled;

    public CommandRouter(
        ISettingsService settings, ITelegramClient telegram, ISystemControlService system,
        IScreenshotService screenshot, ISystemInfoService info, ILogService log)
    {
        _settings = settings;
        _telegram = telegram;
        _system = system;
        _screenshot = screenshot;
        _info = info;
        _log = log;
    }

    public async Task HandleUpdateAsync(TgUpdate update, CancellationToken ct)
    {
        try
        {
            if (update.CallbackQuery is { } cb)
                await HandleTapAsync(cb, ct).ConfigureAwait(false);
            else if (update.Message?.Text is { Length: > 0 })
                await HandleTextAsync(update.Message, ct).ConfigureAwait(false);
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
            await _telegram.AnswerCallbackAsync(cb.Id, "Not authorized", true, ct).ConfigureAwait(false);
            return;
        }

        Count(data);
        _log.Info($"Tap '{data}' from chat {chatId}.");

        var (kind, value) = Split(data);
        switch (kind)
        {
            case "m":
                await ShowScreenAsync(chatId, messageId, value, ct).ConfigureAwait(false);
                await _telegram.AnswerCallbackAsync(cb.Id, null, false, ct).ConfigureAwait(false);
                break;

            case "c":
                var (action, question) = ConfirmationFor(value);
                var confirm = BotMenu.Confirm(value, question);
                await _telegram.EditMessageAsync(chatId, messageId, confirm.Text, confirm.Keyboard, ct).ConfigureAwait(false);
                await _telegram.AnswerCallbackAsync(cb.Id, action, false, ct).ConfigureAwait(false);
                break;

            case "y":
                await RunConfirmedAsync(cb.Id, chatId, messageId, value, ct).ConfigureAwait(false);
                break;

            case "i":
                await AskForValueAsync(cb.Id, chatId, value, ct).ConfigureAwait(false);
                break;

            case "x":
                _prompts.Clear(chatId);
                await _telegram.AnswerCallbackAsync(cb.Id, "Cancelled", false, ct).ConfigureAwait(false);
                await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
                break;

            case "a":
                await RunActionAsync(cb.Id, chatId, value, ct).ConfigureAwait(false);
                break;

            default:
                await _telegram.AnswerCallbackAsync(cb.Id, null, false, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Renders a screen in place when possible, otherwise as a new panel.</summary>
    private async Task ShowScreenAsync(long chatId, long messageId, string screen, CancellationToken ct)
    {
        var view = ScreenFor(screen);
        if (messageId > 0)
        {
            await _telegram.EditMessageAsync(chatId, messageId, view.Text, view.Keyboard, ct).ConfigureAwait(false);
            return;
        }
        await _telegram.SendMessageAsync(chatId, view.Text, view.Keyboard, ct).ConfigureAwait(false);
    }

    private BotMenu.Screen ScreenFor(string screen) => screen switch
    {
        "cap" => BotMenu.Capture(SafeScreenCount()),
        "pwr" => BotMenu.Power(),
        "aud" => BotMenu.Audio(),
        "inp" => BotMenu.Input(),
        "sys" => BotMenu.System(),
        "prc" => BotMenu.Processes(_settings.Current.AllowShellCommands),
        _ => BotMenu.Home(Environment.MachineName),
    };

    private int SafeScreenCount()
    {
        try { return _screenshot.ScreenCount; }
        catch { return 1; }
    }

    private static (string toast, string question) ConfirmationFor(string action) => action switch
    {
        "sd" => ("Confirm shut down", "Shut down this PC?"),
        "rs" => ("Confirm restart", "Restart this PC?"),
        "lo" => ("Confirm sign out", "Sign out of this PC?"),
        "hb" => ("Confirm hibernate", "Hibernate this PC?"),
        _ => ("Confirm", "Are you sure?"),
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
                default: result = "Nothing to do."; break;
            }
            await _telegram.AnswerCallbackAsync(callbackId, result, true, ct).ConfigureAwait(false);
            await _telegram.EditMessageAsync(chatId, messageId,
                $"✅ {TextUtil.Html(result)}", BotMenu.Power().Keyboard, ct).ConfigureAwait(false);
        }
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
            _ => PromptKind.None,
        };
        if (kind == PromptKind.None)
        {
            await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
            return;
        }
        if (kind == PromptKind.ShellCommand && !_settings.Current.AllowShellCommands)
        {
            await _telegram.AnswerCallbackAsync(callbackId,
                "Shell commands are switched off in the desktop app.", true, ct).ConfigureAwait(false);
            return;
        }

        _prompts.Ask(chatId, kind);
        await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
        await _telegram.SendWithMarkupAsync(chatId, ChatPrompts.PromptFor(kind),
            new TgForceReply { Placeholder = ChatPrompts.PlaceholderFor(kind), Selective = true }, ct)
            .ConfigureAwait(false);
    }

    private async Task RunActionAsync(string callbackId, long chatId, string value, CancellationToken ct)
    {
        // Captures and reports arrive as their own message; everything else is a toast.
        switch (value)
        {
            case "ss":
                await _telegram.AnswerCallbackAsync(callbackId, "Capturing…", false, ct).ConfigureAwait(false);
                await SendScreenshotAsync(chatId, null, ct).ConfigureAwait(false);
                return;
            case "sys":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendReportAsync(chatId, () => _info.GetSystemInfoAsync(ct), ct).ConfigureAwait(false);
                return;
            case "disk":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendTextAsync(chatId, _info.GetDisks(), ct).ConfigureAwait(false);
                return;
            case "bat":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendTextAsync(chatId, _info.GetBattery(), ct).ConfigureAwait(false);
                return;
            case "net":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendReportAsync(chatId, () => _info.GetNetworkAsync(ct), ct).ConfigureAwait(false);
                return;
            case "ps":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendTextAsync(chatId, _info.GetTopProcesses(), ct).ConfigureAwait(false);
                return;
            case "clip":
                await _telegram.AnswerCallbackAsync(callbackId, null, false, ct).ConfigureAwait(false);
                await SendClipboardAsync(chatId, ct).ConfigureAwait(false);
                return;
        }

        if (value.StartsWith("ss", StringComparison.Ordinal) && int.TryParse(value[2..], out var index))
        {
            await _telegram.AnswerCallbackAsync(callbackId, "Capturing…", false, ct).ConfigureAwait(false);
            await SendScreenshotAsync(chatId, index.ToString(), ct).ConfigureAwait(false);
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
                default: result = "Nothing to do."; break;
            }
            await _telegram.AnswerCallbackAsync(callbackId, result, false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _telegram.AnswerCallbackAsync(callbackId, ex.Message, true, ct).ConfigureAwait(false);
        }
    }

    // ============================================================
    // Text messages
    // ============================================================

    private async Task HandleTextAsync(TgMessage msg, CancellationToken ct)
    {
        var chatId = msg.Chat?.Id ?? 0;
        if (chatId == 0)
            return;

        var raw = msg.Text!.Trim();
        var (cmd, arg) = ParseCommand(raw);

        if (!IsAuthorized(chatId))
        {
            await HandleUnauthorizedAsync(chatId, cmd, arg, ct).ConfigureAwait(false);
            return;
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

        // Shortcut bar taps arrive as ordinary text.
        switch (raw)
        {
            case BotMenu.BarMenu:
                await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
                return;
            case BotMenu.BarShot:
                Count("shortcut:screenshot");
                await SendScreenshotAsync(chatId, null, ct).ConfigureAwait(false);
                return;
            case BotMenu.BarLock:
                Count("shortcut:lock");
                await SendResultAsync(chatId, () => _system.Lock(), ct).ConfigureAwait(false);
                return;
            case BotMenu.BarPower:
                await ShowScreenAsync(chatId, 0, "pwr", ct).ConfigureAwait(false);
                return;
            case BotMenu.BarStatus:
                Count("shortcut:status");
                await SendReportAsync(chatId, () => _info.GetSystemInfoAsync(ct), ct).ConfigureAwait(false);
                return;
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

    private async Task FulfilPromptAsync(long chatId, PromptKind kind, string input, CancellationToken ct)
    {
        Count("prompt:" + kind);
        switch (kind)
        {
            case PromptKind.Volume:
                if (int.TryParse(input.Trim(), out var level))
                    await SendResultAsync(chatId, () => _system.SetVolume(level), ct).ConfigureAwait(false);
                else
                    await SendTextAsync(chatId, "That is not a number between 0 and 100.", ct).ConfigureAwait(false);
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
        }
    }

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
                await SendTextAsync(chatId, _info.GetDisks(), ct).ConfigureAwait(false);
                break;
            case "battery":
                await SendTextAsync(chatId, _info.GetBattery(), ct).ConfigureAwait(false);
                break;
            case "processes":
            case "ps":
                await SendTextAsync(chatId, _info.GetTopProcesses(), ct).ConfigureAwait(false);
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
                if (int.TryParse(arg, out var pct))
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
            case "whoami":
                await SendTextAsync(chatId, $"Your chat ID: <code>{chatId}</code>", ct).ConfigureAwait(false);
                break;
            case "ping":
                await SendTextAsync(chatId, "🏓 pong", ct).ConfigureAwait(false);
                break;
            default:
                await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
                break;
        }
    }

    // ============================================================
    // Actions that produce output
    // ============================================================

    private async Task SendWelcomeAsync(long chatId, CancellationToken ct)
    {
        await _telegram.SendWithMarkupAsync(chatId,
            $"👋 Connected to <b>{TextUtil.Html(Environment.MachineName)}</b>.\n" +
            "Use the buttons below — no commands to remember.",
            BotMenu.ShortcutBar(), ct).ConfigureAwait(false);
        await ShowScreenAsync(chatId, 0, "home", ct).ConfigureAwait(false);
    }

    private async Task SendScreenshotAsync(long chatId, string? arg, CancellationToken ct)
    {
        try
        {
            byte[] png;
            string caption;
            if (int.TryParse(arg, out var index))
            {
                png = _screenshot.CaptureScreen(index);
                caption = $"🖼 Monitor {index + 1} — {DateTime.Now:HH:mm:ss}";
            }
            else
            {
                png = _screenshot.CaptureAll();
                caption = $"🖼 Desktop — {DateTime.Now:HH:mm:ss}";
            }
            await _telegram.SendPhotoAsync(chatId, png, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png", caption, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, $"❌ Screenshot failed: {TextUtil.Html(ex.Message)}", ct).ConfigureAwait(false);
        }
    }

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
                    $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.txt", "Clipboard", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, $"❌ {TextUtil.Html(ex.Message)}", ct).ConfigureAwait(false);
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
            await SendTextAsync(chatId,
                "🔒 Only web links are allowed. To open files and folders on this PC, " +
                "turn on file access in the Soul Remote app.", ct).ConfigureAwait(false);
            return;
        }

        await SendResultAsync(chatId, () => _system.OpenTarget(trimmed), ct).ConfigureAwait(false);
    }

    /// <summary>Asks for a missing value from a typed command, reusing the button flow's prompt.</summary>
    private async Task AskInChatAsync(long chatId, PromptKind kind, CancellationToken ct)
    {
        _prompts.Ask(chatId, kind);
        await _telegram.SendWithMarkupAsync(chatId, ChatPrompts.PromptFor(kind),
            new TgForceReply { Placeholder = ChatPrompts.PlaceholderFor(kind), Selective = true }, ct)
            .ConfigureAwait(false);
    }

    private async Task RunShellAsync(long chatId, string command, CancellationToken ct)
    {
        if (!_settings.Current.AllowShellCommands)
        {
            await SendTextAsync(chatId, "🔒 Shell commands are switched off in the desktop app.", ct).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            await AskInChatAsync(chatId, PromptKind.ShellCommand, ct).ConfigureAwait(false);
            return;
        }
        try
        {
            var output = await _system.RunShellCommandAsync(command, ct).ConfigureAwait(false);
            var body = TextUtil.Pre(output);
            if (body.Length <= 3500)
                await _telegram.SendMessageAsync(chatId, body, null, ct).ConfigureAwait(false);
            else
                await _telegram.SendDocumentAsync(chatId, Encoding.UTF8.GetBytes(output),
                    $"output_{DateTime.Now:yyyyMMdd_HHmmss}.txt", "Command output", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, $"❌ {TextUtil.Html(ex.Message)}", ct).ConfigureAwait(false);
        }
    }

    private async Task SendResultAsync(long chatId, Func<string> action, CancellationToken ct)
    {
        try { await SendTextAsync(chatId, "✅ " + TextUtil.Html(action()), ct).ConfigureAwait(false); }
        catch (Exception ex) { await SendTextAsync(chatId, "❌ " + TextUtil.Html(ex.Message), ct).ConfigureAwait(false); }
    }

    private async Task SendAsyncResultAsync(long chatId, Func<Task<string>> action, CancellationToken ct)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            await SendTextAsync(chatId, "✅ " + TextUtil.Html(result), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, "❌ " + TextUtil.Html(ex.Message), ct).ConfigureAwait(false);
        }
    }

    private async Task SendReportAsync(long chatId, Func<Task<string>> producer, CancellationToken ct)
    {
        try { await SendTextAsync(chatId, await producer().ConfigureAwait(false), ct).ConfigureAwait(false); }
        catch (Exception ex) { await SendTextAsync(chatId, "❌ " + TextUtil.Html(ex.Message), ct).ConfigureAwait(false); }
    }

    private Task SendTextAsync(long chatId, string text, CancellationToken ct)
        => _telegram.SendMessageAsync(chatId, text, null, ct);

    // ============================================================
    // Pairing and authorization
    // ============================================================

    private async Task HandleUnauthorizedAsync(long chatId, string cmd, string? arg, CancellationToken ct)
    {
        if (cmd != "pair")
        {
            await _telegram.SendMessageAsync(chatId,
                "👋 <b>Soul Remote</b>\n\nThis chat is not linked yet.\n" +
                "Open the Soul Remote app, then send:\n<code>/pair YOURCODE</code>", null, ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(PairingCode) || _failedPairAttempts >= MaxPairAttempts)
        {
            _log.Warning($"Pairing attempt from {chatId} rejected (no active code or too many failures).");
            await _telegram.SendMessageAsync(chatId,
                "⛔ Pairing is closed. Generate a fresh code in the Soul Remote app and try again.",
                null, ct).ConfigureAwait(false);
            return;
        }

        var provided = Encoding.UTF8.GetBytes((arg ?? string.Empty).Trim());
        var expected = Encoding.UTF8.GetBytes(PairingCode);
        if (CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            PairingCode = string.Empty; // single use
            Authorize(chatId);
            _log.Info($"Chat {chatId} authorized via pairing.");
            await SendWelcomeAsync(chatId, ct).ConfigureAwait(false);
        }
        else
        {
            _failedPairAttempts++;
            _log.Warning($"Invalid pairing code from {chatId} ({_failedPairAttempts}/{MaxPairAttempts}).");
            await _telegram.SendMessageAsync(chatId, "❌ That code is not right.", null, ct).ConfigureAwait(false);
        }
    }

    private bool IsAuthorized(long chatId) => _settings.Current.AuthorizedChatIds.Contains(chatId);

    private void Authorize(long chatId)
    {
        if (_settings.Current.AuthorizedChatIds.Contains(chatId))
            return;
        // Clone before mutating: the poll thread reads the live list.
        var settings = _settings.Current.Clone();
        settings.AuthorizedChatIds.Add(chatId);
        _settings.Save(settings);
        ChatAuthorized?.Invoke(chatId);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private void Count(string label)
    {
        Interlocked.Increment(ref _commandsHandled);
        CommandHandled?.Invoke(label);
    }

    private static (string cmd, string? arg) ParseCommand(string raw)
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

    private static (string kind, string value) Split(string data)
    {
        var idx = data.IndexOf(':');
        return idx < 0 ? (data, string.Empty) : (data[..idx], data[(idx + 1)..]);
    }
}
