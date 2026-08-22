using SoulRemote.Models;

namespace SoulRemote.Services;

public enum BotState { Stopped, Starting, Running, Error }

/// <summary>
/// Owns the Telegram long-polling loop and command dispatch lifecycle.
/// Thread-safe start/stop; raises <see cref="StateChanged"/> for the UI.
/// </summary>
public sealed class BotEngine
{
    private readonly ISettingsService _settings;
    private readonly ITelegramClient _telegram;
    private readonly CommandRouter _router;
    private readonly ILogService _log;

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
                throw new InvalidOperationException("Cloudflare is not connected. Deploy the proxy in Settings first.");
            if (!s.HasTelegram)
                throw new InvalidOperationException("Telegram bot token is not set. Add it in Settings.");
            if (s.AuthorizedChatIds.Count == 0)
                _log.Warning("No authorized chats yet — use the pairing code from a Telegram chat to link one.");

            SetState(BotState.Starting);
            LastError = null;

            _telegram.Configure(s.WorkerUrl, s.TelegramBotToken, s.ProxySecret);

            _log.Info("Verifying bot token through the Cloudflare proxy...");
            var me = await _telegram.GetMeAsync().ConfigureAwait(false);
            BotUsername = me.Username;
            _log.Info($"Connected as @{me.Username}.");

            if (!string.Equals(s.TelegramBotUsername, me.Username, StringComparison.Ordinal))
            {
                s.TelegramBotUsername = me.Username ?? string.Empty;
                _settings.Save(s);
            }

            await _telegram.DeleteWebhookAsync().ConfigureAwait(false);

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

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var timeout = Math.Clamp(_settings.Current.PollTimeoutSeconds, 5, 50);
        long offset = 0;
        var backoff = 1;

        // Drop any backlog so stale commands aren't executed on startup.
        try
        {
            var pending = await _telegram.GetUpdatesAsync(-1, 0, ct).ConfigureAwait(false);
            if (pending.Count > 0)
                offset = pending.Max(u => u.UpdateId) + 1;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log.Debug($"Backlog drain skipped: {ex.Message}"); }

        _log.Info("Polling for commands...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _telegram.GetUpdatesAsync(offset, timeout, ct).ConfigureAwait(false);
                backoff = 1;
                foreach (var update in updates)
                {
                    offset = Math.Max(offset, update.UpdateId + 1);
                    await _router.HandleUpdateAsync(update, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning($"Polling error: {ex.Message}. Retrying in {backoff}s.");
                if (State != BotState.Error)
                {
                    LastError = ex.Message;
                    // Keep State=Running so the UI still shows "on"; surface the error text only.
                    StateChanged?.Invoke();
                }
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoff = Math.Min(backoff * 2, 30);
            }
        }
    }

    private async Task NotifyStartupAsync(AppSettings s)
    {
        var text = $"🟢 <b>Soul Remote</b> online on <b>{TextUtil.Html(Environment.MachineName)}</b>.\nUse the buttons below.";
        foreach (var chatId in s.AuthorizedChatIds.ToArray())
        {
            // Re-installs the shortcut bar so the controls are there after a restart.
            try { await _telegram.SendWithMarkupAsync(chatId, text, BotMenu.ShortcutBar()).ConfigureAwait(false); }
            catch (Exception ex) { _log.Debug($"Startup notify to {chatId} failed: {ex.Message}"); }
        }
    }

    private void SetState(BotState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}
