using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using SoulRemote.Models;
using SoulRemote.Services.Security;

namespace SoulRemote.Services;

public enum StepStatus { Pending, Running, Done, Failed, Skipped }

/// <summary>One stage of the connection pipeline, surfaced live in the UI.</summary>
public sealed class ConnectionStep : INotifyPropertyChanged
{
    public ConnectionStep(string title) => Title = title;

    public string Title { get; }

    private string _detail = string.Empty;
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private StepStatus _status = StepStatus.Pending;
    public StepStatus Status { get => _status; set => Set(ref _status, value); }

    public void Reset()
    {
        Status = StepStatus.Pending;
        Detail = string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        // Marshalled so background pipeline stages can update the UI safely.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
        else
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record ConnectionRequest(string CloudflareToken, string WorkerName, string TelegramBotToken);

public sealed record ConnectionResult(bool Success, string? WorkerUrl, string? BotUsername, string? Error);

/// <summary>
/// Runs the whole bring-up in one pass: verify the Cloudflare token, deploy the
/// relay worker, confirm the edge answers, authenticate the bot through that
/// edge, and start listening. Each stage reports into <see cref="Steps"/> so the
/// UI can show exactly where the chain is, and a failure stops the pipeline with
/// the reason attached to the stage that broke.
/// </summary>
public sealed class ConnectionOrchestrator
{
    private readonly ISettingsService _settings;
    private readonly ICloudflareService _cloudflare;
    private readonly ITelegramClient _telegram;
    private readonly BotEngine _bot;
    private readonly ILogService _log;

    private readonly ConnectionStep _verifyToken = new("Verify Cloudflare token");
    private readonly ConnectionStep _resolveAccount = new("Resolve account");
    private readonly ConnectionStep _resolveSubdomain = new("Find workers.dev subdomain");
    private readonly ConnectionStep _deployWorker = new("Deploy relay worker");
    private readonly ConnectionStep _enableRoute = new("Publish public route");
    private readonly ConnectionStep _probeEdge = new("Reach the edge");
    private readonly ConnectionStep _verifyBot = new("Authenticate Telegram bot");
    private readonly ConnectionStep _startRelay = new("Start listening");

    public ObservableCollection<ConnectionStep> Steps { get; }

    private bool _running;
    public bool IsRunning => _running;

    public event Action? Changed;

    public ConnectionOrchestrator(
        ISettingsService settings, ICloudflareService cloudflare,
        ITelegramClient telegram, BotEngine bot, ILogService log)
    {
        _settings = settings;
        _cloudflare = cloudflare;
        _telegram = telegram;
        _bot = bot;
        _log = log;

        Steps = new ObservableCollection<ConnectionStep>
        {
            _verifyToken, _resolveAccount, _resolveSubdomain, _deployWorker,
            _enableRoute, _probeEdge, _verifyBot, _startRelay,
        };
    }

    public async Task<ConnectionResult> RunAsync(ConnectionRequest request, CancellationToken ct = default)
    {
        if (_running)
            return new ConnectionResult(false, null, null, "A connection run is already in progress.");

        if (string.IsNullOrWhiteSpace(request.CloudflareToken))
            return new ConnectionResult(false, null, null, "Paste your Cloudflare API token first.");
        if (string.IsNullOrWhiteSpace(request.TelegramBotToken))
            return new ConnectionResult(false, null, null, "Paste your Telegram bot token first.");

        _running = true;
        foreach (var step in Steps)
            step.Reset();
        Changed?.Invoke();

        var cfToken = request.CloudflareToken.Trim();
        var botToken = request.TelegramBotToken.Trim();
        var workerName = _cloudflare.NormalizeWorkerName(request.WorkerName);
        ConnectionStep? current = null;

        try
        {
            // A secret is minted once and reused, so the worker never becomes an open relay.
            var settings = _settings.Current.Clone();
            if (string.IsNullOrWhiteSpace(settings.ProxySecret))
                settings.ProxySecret = SecureRandom.Token(28);
            settings.CloudflareApiToken = cfToken;
            settings.TelegramBotToken = botToken;
            settings.WorkerName = workerName;
            _settings.Save(settings);

            current = Begin(_verifyToken);
            await _cloudflare.VerifyTokenAsync(cfToken, ct).ConfigureAwait(false);
            Complete(_verifyToken, "Token is active");

            current = Begin(_resolveAccount);
            var accounts = await _cloudflare.GetAccountsAsync(cfToken, ct).ConfigureAwait(false);
            var saved = _settings.Current.CloudflareAccountId;
            var account = accounts.FirstOrDefault(a => a.Id == saved) ?? accounts[0];
            Complete(_resolveAccount, account.Name);

            current = Begin(_resolveSubdomain);
            var subdomain = await _cloudflare.GetWorkersDevSubdomainAsync(cfToken, account.Id, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(subdomain))
                throw new InvalidOperationException(
                    "This account has no workers.dev subdomain yet. Open Cloudflare -> Workers & Pages once to claim one, then run Connect again.");
            Complete(_resolveSubdomain, $"{subdomain}.workers.dev");

            current = Begin(_deployWorker);
            var proxySecret = _settings.Current.ProxySecret;
            await _cloudflare.UploadWorkerAsync(cfToken, account.Id, workerName, proxySecret, ct).ConfigureAwait(false);
            Complete(_deployWorker, workerName);

            current = Begin(_enableRoute);
            await _cloudflare.EnableSubdomainRouteAsync(cfToken, account.Id, workerName, ct).ConfigureAwait(false);
            var workerUrl = $"https://{workerName}.{subdomain}.workers.dev";
            Complete(_enableRoute, workerUrl);

            current = Begin(_probeEdge);
            var reachable = await _cloudflare.ProbeWorkerAsync(workerUrl, proxySecret, ct).ConfigureAwait(false);
            if (reachable)
                Complete(_probeEdge, "Edge is answering");
            else
                // Propagation can lag; the bot check below is the real proof, so this never blocks.
                Warn(_probeEdge, "Still propagating — continuing");

            // Persist everything Cloudflare-side before touching Telegram.
            settings = _settings.Current.Clone();
            settings.CloudflareAccountId = account.Id;
            settings.CloudflareAccountName = account.Name;
            settings.WorkersDevSubdomain = subdomain;
            settings.WorkerUrl = workerUrl;
            _settings.Save(settings);

            current = Begin(_verifyBot);
            _telegram.Configure(workerUrl, botToken, proxySecret);
            var me = await _telegram.GetMeAsync(ct).ConfigureAwait(false);
            settings = _settings.Current.Clone();
            settings.TelegramBotUsername = me.Username ?? string.Empty;
            _settings.Save(settings);
            Complete(_verifyBot, $"@{me.Username}");

            current = Begin(_startRelay);
            await _bot.StartAsync().ConfigureAwait(false);
            Complete(_startRelay, "Listening for commands");

            _log.Info($"Relay is up: {workerUrl} as @{me.Username}.");
            return new ConnectionResult(true, workerUrl, me.Username, null);
        }
        catch (OperationCanceledException)
        {
            if (current is not null)
                Fail(current, "Cancelled");
            return new ConnectionResult(false, null, null, "Connection cancelled.");
        }
        catch (Exception ex)
        {
            if (current is not null)
                Fail(current, ex.Message);
            _log.Error("Connection failed", ex);
            return new ConnectionResult(false, null, null, ex.Message);
        }
        finally
        {
            _running = false;
            Changed?.Invoke();
        }
    }

    private ConnectionStep Begin(ConnectionStep step)
    {
        step.Status = StepStatus.Running;
        step.Detail = string.Empty;
        Changed?.Invoke();
        return step;
    }

    private void Complete(ConnectionStep step, string detail)
    {
        step.Status = StepStatus.Done;
        step.Detail = detail;
        Changed?.Invoke();
    }

    private void Warn(ConnectionStep step, string detail)
    {
        step.Status = StepStatus.Skipped;
        step.Detail = detail;
        Changed?.Invoke();
    }

    private void Fail(ConnectionStep step, string detail)
    {
        step.Status = StepStatus.Failed;
        step.Detail = detail;
        Changed?.Invoke();
    }
}
