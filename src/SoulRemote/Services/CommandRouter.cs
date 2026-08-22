using System.Security.Cryptography;
using System.Text;
using SoulRemote.Models;

namespace SoulRemote.Services;

/// <summary>
/// Interprets incoming Telegram updates and executes the matching system action,
/// enforcing the authorized-chat whitelist and the pairing bootstrap.
/// </summary>
public sealed class CommandRouter
{
    private const int ShutdownDelaySeconds = 15;

    private readonly ISettingsService _settings;
    private readonly ITelegramClient _telegram;
    private readonly ISystemControlService _system;
    private readonly IScreenshotService _screenshot;
    private readonly ISystemInfoService _info;
    private readonly ILogService _log;

    private const int MaxPairAttempts = 5;
    private string _pairingCode = string.Empty;
    private int _failedPairAttempts;

    /// <summary>One-time pairing code shown in the desktop app UI. Setting it resets the lockout.</summary>
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
                await HandleCallbackAsync(cb, ct).ConfigureAwait(false);
            else if (update.Message?.Text is { Length: > 0 })
                await HandleTextAsync(update.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error("Error handling update", ex);
        }
    }

    // ---- text commands ----

    private async Task HandleTextAsync(TgMessage msg, CancellationToken ct)
    {
        var chatId = msg.Chat?.Id ?? 0;
        if (chatId == 0) return;

        var raw = msg.Text!.Trim();
        var (cmd, arg) = ParseCommand(raw);

        // Pairing / bootstrap handling before the auth gate.
        if (cmd is "pair" or "start" && !IsAuthorized(chatId))
        {
            await HandleUnauthorizedAsync(chatId, cmd, arg, ct).ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(chatId))
        {
            _log.Warning($"Rejected command '{cmd}' from unauthorized chat {chatId}.");
            await _telegram.SendMessageAsync(chatId,
                "⛔ You are not authorized. Send <code>/pair &lt;code&gt;</code> with the code shown in the Soul Remote app.", null, ct)
                .ConfigureAwait(false);
            return;
        }

        _log.Info($"Command '{cmd}' from chat {chatId}.");

        switch (cmd)
        {
            case "start":
            case "menu":
            case "help":
                await SendMainMenuAsync(chatId, WelcomeText(), ct);
                break;
            case "screenshot":
            case "ss":
                await SendScreenshotAsync(chatId, arg, ct);
                break;
            case "sysinfo":
            case "info":
                await RunTextAsync(chatId, () => _info.GetSystemInfoAsync(ct), ct);
                break;
            case "disks":
                await ReplyAsync(chatId, _info.GetDisks(), ct);
                break;
            case "battery":
                await ReplyAsync(chatId, _info.GetBattery(), ct);
                break;
            case "processes":
            case "ps":
                await ReplyAsync(chatId, _info.GetTopProcesses(), ct);
                break;
            case "network":
            case "net":
                await RunTextAsync(chatId, () => _info.GetNetworkAsync(ct), ct);
                break;
            case "lock":
                await SafeRunAsync(chatId, () => _system.Lock(), ct);
                break;
            case "sleep":
                await SafeRunAsync(chatId, () => _system.Sleep(), ct);
                break;
            case "hibernate":
                await ConfirmAsync(chatId, "hibernate", "💤 Hibernate this machine?", ct);
                break;
            case "monitoroff":
                await SafeRunAsync(chatId, () => _system.MonitorOff(), ct);
                break;
            case "shutdown":
                await ConfirmAsync(chatId, "shutdown", "⏻ Shut down this machine?", ct);
                break;
            case "restart":
            case "reboot":
                await ConfirmAsync(chatId, "restart", "🔄 Restart this machine?", ct);
                break;
            case "logoff":
                await ConfirmAsync(chatId, "logoff", "🚪 Log off the current user?", ct);
                break;
            case "cancel":
                await SafeRunAsyncTask(chatId, () => _system.CancelPendingAsync(), ct);
                break;
            case "volume":
            case "vol":
                await HandleVolumeAsync(chatId, arg, ct);
                break;
            case "volup":
                await SafeRunAsync(chatId, () => _system.VolumeUp(), ct);
                break;
            case "voldown":
                await SafeRunAsync(chatId, () => _system.VolumeDown(), ct);
                break;
            case "mute":
                await SafeRunAsync(chatId, () => _system.ToggleMute(), ct);
                break;
            case "play":
                await SafeRunAsync(chatId, () => _system.MediaPlayPause(), ct);
                break;
            case "next":
                await SafeRunAsync(chatId, () => _system.MediaNext(), ct);
                break;
            case "prev":
                await SafeRunAsync(chatId, () => _system.MediaPrevious(), ct);
                break;
            case "kill":
                await HandleKillAsync(chatId, arg, ct);
                break;
            case "cmd":
                await HandleCmdAsync(chatId, arg, ct);
                break;
            case "whoami":
                await ReplyAsync(chatId, $"Your chat ID: <code>{chatId}</code>", ct);
                break;
            case "ping":
                await ReplyAsync(chatId, "🏓 pong", ct);
                break;
            default:
                await ReplyAsync(chatId, "Unknown command. Send /help for the menu.", ct);
                break;
        }
    }

    // ---- callbacks (inline buttons) ----

    private async Task HandleCallbackAsync(TgCallbackQuery cb, CancellationToken ct)
    {
        var chatId = cb.Message?.Chat?.Id ?? cb.From?.Id ?? 0;
        var data = cb.Data ?? string.Empty;

        if (chatId == 0 || !IsAuthorized(chatId))
        {
            await _telegram.AnswerCallbackAsync(cb.Id, "Not authorized", ct);
            return;
        }

        await _telegram.AnswerCallbackAsync(cb.Id, null, ct);
        _log.Info($"Callback '{data}' from chat {chatId}.");

        var (kind, value) = ParseCallback(data);
        switch (kind)
        {
            case "m" when value == "main":
                await SendMainMenuAsync(chatId, "Main menu:", ct);
                break;
            case "m" when value == "power":
                await SendPowerMenuAsync(chatId, ct);
                break;
            case "a":
                await DispatchActionAsync(chatId, value, ct);
                break;
            case "y": // confirmed destructive action
                await DispatchConfirmedAsync(chatId, value, ct);
                break;
            default:
                break;
        }
    }

    private async Task DispatchActionAsync(long chatId, string value, CancellationToken ct)
    {
        switch (value)
        {
            case "screenshot": await SendScreenshotAsync(chatId, null, ct); break;
            case "sysinfo": await RunTextAsync(chatId, () => _info.GetSystemInfoAsync(ct), ct); break;
            case "disks": await ReplyAsync(chatId, _info.GetDisks(), ct); break;
            case "battery": await ReplyAsync(chatId, _info.GetBattery(), ct); break;
            case "proc": await ReplyAsync(chatId, _info.GetTopProcesses(), ct); break;
            case "net": await RunTextAsync(chatId, () => _info.GetNetworkAsync(ct), ct); break;
            case "lock": await SafeRunAsync(chatId, () => _system.Lock(), ct); break;
            case "sleep": await SafeRunAsync(chatId, () => _system.Sleep(), ct); break;
            case "monoff": await SafeRunAsync(chatId, () => _system.MonitorOff(), ct); break;
            case "volup": await SafeRunAsync(chatId, () => _system.VolumeUp(), ct); break;
            case "voldown": await SafeRunAsync(chatId, () => _system.VolumeDown(), ct); break;
            case "mute": await SafeRunAsync(chatId, () => _system.ToggleMute(), ct); break;
            case "play": await SafeRunAsync(chatId, () => _system.MediaPlayPause(), ct); break;
            case "next": await SafeRunAsync(chatId, () => _system.MediaNext(), ct); break;
            case "prev": await SafeRunAsync(chatId, () => _system.MediaPrevious(), ct); break;
            case "cancelpending": await SafeRunAsyncTask(chatId, () => _system.CancelPendingAsync(), ct); break;
            case "shutdown": await ConfirmAsync(chatId, "shutdown", "⏻ Shut down this machine?", ct); break;
            case "restart": await ConfirmAsync(chatId, "restart", "🔄 Restart this machine?", ct); break;
            case "logoff": await ConfirmAsync(chatId, "logoff", "🚪 Log off the current user?", ct); break;
            case "hibernate": await ConfirmAsync(chatId, "hibernate", "💤 Hibernate this machine?", ct); break;
            default: break;
        }
    }

    private async Task DispatchConfirmedAsync(long chatId, string value, CancellationToken ct)
    {
        switch (value)
        {
            case "shutdown": await SafeRunAsyncTask(chatId, () => _system.ShutdownAsync(ShutdownDelaySeconds), ct); break;
            case "restart": await SafeRunAsyncTask(chatId, () => _system.RestartAsync(ShutdownDelaySeconds), ct); break;
            case "logoff": await SafeRunAsyncTask(chatId, () => _system.LogoffAsync(), ct); break;
            case "hibernate": await SafeRunAsync(chatId, () => _system.Hibernate(), ct); break;
            default: break;
        }
    }

    // ---- action helpers ----

    private async Task SendScreenshotAsync(long chatId, string? arg, CancellationToken ct)
    {
        try
        {
            byte[] png;
            string caption;
            if (int.TryParse(arg, out var index))
            {
                png = _screenshot.CaptureScreen(index);
                caption = $"🖼 Screen {index} — {DateTime.Now:HH:mm:ss}";
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
            await ReplyAsync(chatId, $"❌ Screenshot failed: {TextUtil.Html(ex.Message)}", ct);
        }
    }

    private async Task HandleVolumeAsync(long chatId, string? arg, CancellationToken ct)
    {
        if (int.TryParse(arg, out var percent))
            await SafeRunAsync(chatId, () => _system.SetVolume(percent), ct);
        else
            await ReplyAsync(chatId, "Usage: <code>/volume 0-100</code>", ct);
    }

    private async Task HandleKillAsync(long chatId, string? arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await ReplyAsync(chatId, "Usage: <code>/kill &lt;name|pid&gt;</code>", ct);
            return;
        }
        await SafeRunAsync(chatId, () => _system.KillProcess(arg!), ct);
    }

    private async Task HandleCmdAsync(long chatId, string? arg, CancellationToken ct)
    {
        if (!_settings.Current.AllowShellCommands)
        {
            await ReplyAsync(chatId, "🔒 Shell commands are disabled. Enable them in the Soul Remote app settings.", ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(arg))
        {
            await ReplyAsync(chatId, "Usage: <code>/cmd &lt;command&gt;</code>", ct);
            return;
        }
        try
        {
            var output = await _system.RunShellCommandAsync(arg!, ct).ConfigureAwait(false);
            var pre = TextUtil.Pre(output);
            // HTML escaping can multiply the length; if the single <pre> block would exceed
            // Telegram's message limit (and risk being split mid-tag), send it as a text file.
            if (pre.Length <= 3500)
            {
                await _telegram.SendMessageAsync(chatId, pre, null, ct).ConfigureAwait(false);
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(output);
                await _telegram.SendDocumentAsync(chatId, bytes,
                    $"output_{DateTime.Now:yyyyMMdd_HHmmss}.txt", "Command output", ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await ReplyAsync(chatId, $"❌ {TextUtil.Html(ex.Message)}", ct);
        }
    }

    private async Task HandleUnauthorizedAsync(long chatId, string cmd, string? arg, CancellationToken ct)
    {
        if (cmd == "pair")
        {
            if (string.IsNullOrEmpty(PairingCode) || _failedPairAttempts >= MaxPairAttempts)
            {
                _log.Warning($"Pairing attempt from {chatId} rejected (no active code or too many failures).");
                await _telegram.SendMessageAsync(chatId,
                    "⛔ Pairing is not available right now. Generate a fresh code in the Soul Remote app and try again.",
                    null, ct).ConfigureAwait(false);
                return;
            }

            var provided = Encoding.UTF8.GetBytes((arg ?? string.Empty).Trim());
            var expected = Encoding.UTF8.GetBytes(PairingCode);
            if (CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                PairingCode = string.Empty; // one-time use: invalidate immediately
                Authorize(chatId);
                await _telegram.SendMessageAsync(chatId,
                    "✅ Paired successfully! This chat can now control the machine. Send /menu.", null, ct)
                    .ConfigureAwait(false);
                _log.Info($"Chat {chatId} authorized via pairing.");
            }
            else
            {
                _failedPairAttempts++;
                _log.Warning($"Invalid pairing code from {chatId} ({_failedPairAttempts}/{MaxPairAttempts}).");
                await _telegram.SendMessageAsync(chatId, "❌ Invalid pairing code.", null, ct).ConfigureAwait(false);
            }
            return;
        }

        // /start from an unknown chat
        await _telegram.SendMessageAsync(chatId,
            "👋 <b>Soul Remote</b>\n\nThis chat is not authorized yet.\n" +
            "Open the Soul Remote app, copy the pairing code, then send:\n" +
            "<code>/pair YOURCODE</code>", null, ct).ConfigureAwait(false);
    }

    // ---- menus ----

    private Task SendMainMenuAsync(long chatId, string text, CancellationToken ct)
    {
        var kb = new TgInlineKeyboardMarkup
        {
            InlineKeyboard = new()
            {
                Row(("📸 Screenshot", "a:screenshot"), ("🖥 Sys Info", "a:sysinfo")),
                Row(("🔒 Lock", "a:lock"), ("🌙 Sleep", "a:sleep"), ("🖤 Screen off", "a:monoff")),
                Row(("🔉 Vol-", "a:voldown"), ("🔇 Mute", "a:mute"), ("🔊 Vol+", "a:volup")),
                Row(("⏮", "a:prev"), ("⏯", "a:play"), ("⏭", "a:next")),
                Row(("💽 Disks", "a:disks"), ("🔋 Battery", "a:battery")),
                Row(("📋 Processes", "a:proc"), ("🌐 Network", "a:net")),
                Row(("⚡ Power options", "m:power")),
            },
        };
        return _telegram.SendMessageAsync(chatId, text, kb, ct);
    }

    private Task SendPowerMenuAsync(long chatId, CancellationToken ct)
    {
        var kb = new TgInlineKeyboardMarkup
        {
            InlineKeyboard = new()
            {
                Row(("⏻ Shutdown", "a:shutdown"), ("🔄 Restart", "a:restart")),
                Row(("🚪 Log off", "a:logoff"), ("💤 Hibernate", "a:hibernate")),
                Row(("✋ Cancel pending", "a:cancelpending")),
                Row(("⬅ Back", "m:main")),
            },
        };
        return _telegram.SendMessageAsync(chatId, "⚡ <b>Power options</b>", kb, ct);
    }

    private Task ConfirmAsync(long chatId, string action, string question, CancellationToken ct)
    {
        var kb = new TgInlineKeyboardMarkup
        {
            InlineKeyboard = new()
            {
                Row(("✅ Confirm", $"y:{action}"), ("❌ Cancel", "m:main")),
            },
        };
        return _telegram.SendMessageAsync(chatId, $"⚠️ {question}", kb, ct);
    }

    // ---- generic runners ----

    private async Task SafeRunAsync(long chatId, Func<string> action, CancellationToken ct)
    {
        try
        {
            var result = action();
            await ReplyAsync(chatId, "✅ " + TextUtil.Html(result), ct);
        }
        catch (Exception ex)
        {
            await ReplyAsync(chatId, "❌ " + TextUtil.Html(ex.Message), ct);
        }
    }

    private async Task SafeRunAsyncTask(long chatId, Func<Task<string>> action, CancellationToken ct)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            await ReplyAsync(chatId, "✅ " + TextUtil.Html(result), ct);
        }
        catch (Exception ex)
        {
            await ReplyAsync(chatId, "❌ " + TextUtil.Html(ex.Message), ct);
        }
    }

    private async Task RunTextAsync(long chatId, Func<Task<string>> producer, CancellationToken ct)
    {
        try
        {
            var text = await producer().ConfigureAwait(false);
            await ReplyAsync(chatId, text, ct);
        }
        catch (Exception ex)
        {
            await ReplyAsync(chatId, "❌ " + TextUtil.Html(ex.Message), ct);
        }
    }

    private Task ReplyAsync(long chatId, string text, CancellationToken ct)
        => _telegram.SendMessageAsync(chatId, text, null, ct);

    // ---- auth ----

    private bool IsAuthorized(long chatId) => _settings.Current.AuthorizedChatIds.Contains(chatId);

    private void Authorize(long chatId)
    {
        if (_settings.Current.AuthorizedChatIds.Contains(chatId))
            return;
        // Clone before mutating so the live list (read by IsAuthorized on the poll thread) is
        // never modified in place; Save swaps in the new snapshot atomically.
        var settings = _settings.Current.Clone();
        settings.AuthorizedChatIds.Add(chatId);
        _settings.Save(settings);
        ChatAuthorized?.Invoke(chatId);
    }

    // ---- parsing + keyboard helpers ----

    private static (string cmd, string? arg) ParseCommand(string raw)
    {
        if (!raw.StartsWith('/'))
            return (raw.ToLowerInvariant(), null);
        var body = raw[1..];
        var spaceIdx = body.IndexOf(' ');
        var cmdPart = spaceIdx < 0 ? body : body[..spaceIdx];
        var arg = spaceIdx < 0 ? null : body[(spaceIdx + 1)..].Trim();
        // Strip @botusername suffix.
        var at = cmdPart.IndexOf('@');
        if (at >= 0) cmdPart = cmdPart[..at];
        return (cmdPart.ToLowerInvariant(), string.IsNullOrEmpty(arg) ? null : arg);
    }

    private static (string kind, string value) ParseCallback(string data)
    {
        var idx = data.IndexOf(':');
        return idx < 0 ? (data, string.Empty) : (data[..idx], data[(idx + 1)..]);
    }

    private static List<TgInlineKeyboardButton> Row(params (string text, string data)[] buttons)
        => buttons.Select(b => new TgInlineKeyboardButton(b.text, b.data)).ToList();

    private static string WelcomeText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("👋 <b>Soul Remote</b> — remote control connected.");
        sb.AppendLine();
        sb.AppendLine("Use the buttons below, or type commands:");
        sb.AppendLine("/screenshot, /sysinfo, /disks, /battery, /processes, /network");
        sb.AppendLine("/lock, /sleep, /hibernate, /shutdown, /restart, /logoff, /cancel");
        sb.AppendLine("/volume 0-100, /volup, /voldown, /mute, /play, /next, /prev");
        sb.AppendLine("/kill &lt;name|pid&gt;, /cmd &lt;command&gt;, /whoami, /ping");
        return sb.ToString();
    }
}
