using SoulRemote.Localization;
using SoulRemote.Models;

namespace SoulRemote.Services;

public enum BotState { Stopped, Starting, Running, Error }

/// <summary>
/// Owns the Telegram long-polling loop and command dispatch lifecycle.
/// Thread-safe start/stop; raises <see cref="StateChanged"/> for the UI.
/// </summary>
public sealed class BotEngine
{
    /// <summary>Telegram's own code for "another getUpdates is already running".</summary>
    private const int ConflictErrorCode = 409;

    private readonly ISettingsService _settings;
    private readonly ITelegramClient _telegram;
    private readonly CommandRouter _router;
    private readonly ILogService _log;
    private readonly UpdateDispatcher _dispatcher;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public BotState State { get; private set; } = BotState.Stopped;
    public string? BotUsername { get; private set; }
    public string? LastError { get; private set; }

    public event Action? StateChanged;

    public BotEngine(ISettingsService settings, ITelegramClient telegram, CommandRouter router, ILogService log)
    {
        _settings = settings;
        _telegram = telegram;
        _router = router;
        _log = log;
        _dispatcher = new UpdateDispatcher(log);
    }

    public bool IsRunning => State is BotState.Running or BotState.Starting;

    public async Task StartAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning)
                return;

            var s = _settings.Current;
            if (!s.HasCloudflare)
                throw new InvalidOperationException(Strings.Get("err.nocloudflare"));
            if (!s.HasTelegram)
                throw new InvalidOperationException(Strings.Get("err.notelegram"));
            if (s.AuthorizedChatIds.Count == 0)
                _log.Warning(Strings.Get("err.nochats"));

            SetState(BotState.Starting);
            LastError = null;

            _telegram.Configure(s.WorkerUrl, s.TelegramBotToken, s.ProxySecret);

            _log.Info("Verifying bot token through the Cloudflare proxy...");
            var me = await _telegram.GetMeAsync().ConfigureAwait(false);
            BotUsername = me.Username;
            _log.Info($"Connected as @{me.Username}.");

            if (!string.Equals(_settings.Current.TelegramBotUsername, me.Username, StringComparison.Ordinal))
            {
                var updated = _settings.Current.Clone();
                updated.TelegramBotUsername = me.Username ?? string.Empty;
                _settings.Save(updated);
            }

            await _telegram.DeleteWebhookAsync().ConfigureAwait(false);
            await _telegram.SetMyCommandsAsync(TypedCommands()).ConfigureAwait(false);

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => PollLoopAsync(_cts.Token));

            SetState(BotState.Running);

            if (s.NotifyOnStartup)
                await NotifyStartupAsync(s).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetState(BotState.Error);
            _log.Error("Failed to start bot", ex);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cts is null)
            {
                SetState(BotState.Stopped);
                return;
            }
            _log.Info("Stopping bot...");
            _cts.Cancel();
            if (_loopTask is not null)
            {
                try { await _loopTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex) { _log.Debug($"Loop ended: {ex.Message}"); }
            }
            // Give commands already running a moment to finish reporting before the
            // app tears down around them.
            await _dispatcher.DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
            _loopTask = null;
            SetState(BotState.Stopped);
            _log.Info("Bot stopped.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>The list Telegram shows behind the "/" button, in the current language.</summary>
    private static IReadOnlyList<(string, string)> TypedCommands() => new[]
    {
        ("menu", Strings.Get("cmd.menu")),
        ("screenshot", Strings.Get("cmd.screenshot")),
        ("lock", Strings.Get("cmd.lock")),
        ("sysinfo", Strings.Get("cmd.sysinfo")),
        ("disks", Strings.Get("cmd.disks")),
        ("battery", Strings.Get("cmd.battery")),
        ("network", Strings.Get("cmd.network")),
        ("processes", Strings.Get("cmd.processes")),
        ("volume", Strings.Get("cmd.volume")),
        ("clipboard", Strings.Get("cmd.clipboard")),
        ("cancel", Strings.Get("cmd.cancel")),
        ("lang", Strings.Get("cmd.lang")),
        ("settings", Strings.Get("cmd.settings")),
        ("ping", Strings.Get("cmd.ping")),
    };

    private async Task PollLoopAsync(CancellationToken ct)
    {
        long offset = 0;
        var backoff = 1;

        // Drop any backlog so stale commands aren't executed on startup.
        try
        {
            var pending = await _telegram.GetUpdatesAsync(-1, 0, ct).ConfigureAwait(false);
            if (pending.Count > 0)
                offset = pending.Max(u => u.UpdateId) + 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { _log.Debug($"Backlog drain skipped: {ex.Message}"); }

        _log.Info("Polling for commands...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Re-read each pass so changing it in Settings takes effect on the next
                // poll instead of waiting for the relay to be restarted.
                var timeout = Math.Clamp(_settings.Current.PollTimeoutSeconds, 5, 50);

                var updates = await _telegram.GetUpdatesAsync(offset, timeout, ct).ConfigureAwait(false);
                backoff = 1;
                if (LastError is not null)
                {
                    // Polling recovered; stop showing the old error.
                    LastError = null;
                    StateChanged?.Invoke();
                }
                foreach (var update in updates)
                {
                    offset = Math.Max(offset, update.UpdateId + 1);
                    // Queued rather than awaited: a slow command must not hold up the
                    // next getUpdates, or every other chat waits behind it.
                    var captured = update;
                    _dispatcher.Enqueue(ChatIdOf(captured), () => _router.HandleUpdateAsync(captured, ct));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (TelegramApiException ex) when (ex.ErrorCode == ConflictErrorCode)
            {
                // Two copies of Soul Remote polling the same bot token will take turns
                // stealing each other's updates forever. Retrying faster makes it worse,
                // so back off hard and say plainly what is wrong.
                _log.Error("Another Soul Remote (or another program) is already polling this bot token. " +
                           "Stop the other one, or use a separate bot for this PC.");
                LastError = Strings.Get("err.conflict");
                StateChanged?.Invoke();
                backoff = 30;
                if (!await DelayAsync(backoff, ct).ConfigureAwait(false))
                    break;
            }
            catch (Exception ex)
            {
                // A long-poll that times out lands here too, and must retry rather than end.
                _log.Warning($"Polling error: {ex.Message}. Retrying in {backoff}s.");
                if (State != BotState.Error)
                {
                    LastError = ex.Message;
                    // Keep State=Running so the UI still shows "on"; surface the error text only.
                    StateChanged?.Invoke();
                }
                if (!await DelayAsync(backoff, ct).ConfigureAwait(false))
                    break;
                backoff = Math.Min(backoff * 2, 30);
            }
        }
    }

    private static async Task<bool> DelayAsync(int seconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Which chat an update belongs to, so its work is serialised against that chat's
    /// other work. An update with no identifiable chat gets its own bucket rather than
    /// sharing bucket zero with every other stray.
    /// </summary>
    private static long ChatIdOf(TgUpdate update) =>
        update.Message?.Chat?.Id
        ?? update.CallbackQuery?.Message?.Chat?.Id
        ?? update.CallbackQuery?.From?.Id
        ?? update.UpdateId;

    private async Task NotifyStartupAsync(AppSettings s)
    {
        var text = Strings.Format("bot.online", TextUtil.Html(Environment.MachineName));
        foreach (var chatId in s.AuthorizedChatIds.ToArray())
        {
            // Re-installs the shortcut bar so the controls are there after a restart.
            try { await _telegram.SendWithMarkupAsync(chatId, text, BotMenu.ShortcutBar()).ConfigureAwait(false); }
            catch (Exception ex) { _log.Debug($"Startup notify to {chatId} failed: {ex.Message}"); }
        }
    }

    /// <summary>Re-publishes the "/" command list after a language change.</summary>
    public async Task RefreshCommandListAsync()
    {
        if (!IsRunning)
            return;
        try { await _telegram.SetMyCommandsAsync(TypedCommands()).ConfigureAwait(false); }
        catch (Exception ex) { _log.Debug($"setMyCommands after language change: {ex.Message}"); }
    }

    private void SetState(BotState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}
