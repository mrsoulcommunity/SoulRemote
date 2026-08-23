using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SoulRemote.Abstractions;
using SoulRemote.Localization;
using SoulRemote.Models;
using SoulRemote.Services.Security;

namespace SoulRemote.Services;

public enum StepStatus { Pending, Running, Done, Failed, Skipped }

/// <summary>One stage of the connection pipeline, surfaced live in the UI.</summary>
public sealed class ConnectionStep : INotifyPropertyChanged
{
    private readonly string _titleKey;
    private readonly IUiDispatcher _dispatcher;

    public ConnectionStep(string titleKey, IUiDispatcher dispatcher)
    {
        _titleKey = titleKey;
        _dispatcher = dispatcher;
    }

    /// <summary>Resolved on read so a language change re-titles the pipeline in place.</summary>
    public string Title => Strings.Get(_titleKey);

    /// <summary>Re-reads the title after the language changes.</summary>
    public void RefreshTitle() => _dispatcher.Post(
        () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))));

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
        _dispatcher.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
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

    private readonly ConnectionStep _verifyToken;
    private readonly ConnectionStep _resolveAccount;
    private readonly ConnectionStep _resolveSubdomain;
    private readonly ConnectionStep _deployWorker;
    private readonly ConnectionStep _enableRoute;
    private readonly ConnectionStep _probeEdge;
    private readonly ConnectionStep _verifyBot;
    private readonly ConnectionStep _startRelay;

    public ObservableCollection<ConnectionStep> Steps { get; }

    private bool _running;
    public bool IsRunning => _running;

    public event Action? Changed;

    public ConnectionOrchestrator(
        ISettingsService settings, ICloudflareService cloudflare,
        ITelegramClient telegram, BotEngine bot, ILogService log, IUiDispatcher? dispatcher = null)
    {
        _settings = settings;
        _cloudflare = cloudflare;
        _telegram = telegram;
        _bot = bot;
        _log = log;

        var ui = dispatcher ?? ImmediateDispatcher.Instance;
        _verifyToken = new ConnectionStep("ui.step.verify", ui);
        _resolveAccount = new ConnectionStep("ui.step.account", ui);
        _resolveSubdomain = new ConnectionStep("ui.step.subdomain", ui);
        _deployWorker = new ConnectionStep("ui.step.deploy", ui);
        _enableRoute = new ConnectionStep("ui.step.route", ui);
        _probeEdge = new ConnectionStep("ui.step.probe", ui);
        _verifyBot = new ConnectionStep("ui.step.bot", ui);
        _startRelay = new ConnectionStep("ui.step.listen", ui);

        Steps = new ObservableCollection<ConnectionStep>
        {
            _verifyToken, _resolveAccount, _resolveSubdomain, _deployWorker,
            _enableRoute, _probeEdge, _verifyBot, _startRelay,
        };
    }

    /// <summary>Re-titles the pipeline after a language change.</summary>
    public void RefreshLanguage()
    {
        foreach (var step in Steps)
            step.RefreshTitle();
    }

    public async Task<ConnectionResult> RunAsync(ConnectionRequest request, CancellationToken ct = default)
    {
        if (_running)
            return new ConnectionResult(false, null, null, Strings.Get("ui.connect.inflight"));

        if (string.IsNullOrWhiteSpace(request.CloudflareToken))
            return new ConnectionResult(false, null, null, Strings.Get("ui.connect.needcf"));
        if (string.IsNullOrWhiteSpace(request.TelegramBotToken))
            return new ConnectionResult(false, null, null, Strings.Get("ui.connect.needtg"));

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
            // Credentials are NOT written yet: a mistyped token must not overwrite a working
            // configuration before it has been verified.
            var settings = _settings.Current.Clone();
            if (string.IsNullOrWhiteSpace(settings.ProxySecret))
                settings.ProxySecret = SecureRandom.Token(28);
            settings.WorkerName = workerName;
            _settings.Save(settings);

            current = Begin(_verifyToken);
            await _cloudflare.VerifyTokenAsync(cfToken, ct).ConfigureAwait(false);
            var verified = _settings.Current.Clone();
            verified.CloudflareApiToken = cfToken;
            _settings.Save(verified);
            Complete(_verifyToken, Strings.Get("ui.step.tokenactive"));

            current = Begin(_resolveAccount);
            var accounts = await _cloudflare.GetAccountsAsync(cfToken, ct).ConfigureAwait(false);
            var saved = _settings.Current.CloudflareAccountId;
            var account = accounts.FirstOrDefault(a => a.Id == saved) ?? accounts[0];
            Complete(_resolveAccount, account.Name);

            current = Begin(_resolveSubdomain);
            var subdomain = await _cloudflare.GetWorkersDevSubdomainAsync(cfToken, account.Id, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(subdomain))
                throw new InvalidOperationException(Strings.Get("err.cf.nosubdomain"));
            Complete(_resolveSubdomain, $"{subdomain}.workers.dev");

            current = Begin(_deployWorker);
            var proxySecret = settings.ProxySecret;
            if (string.IsNullOrWhiteSpace(proxySecret))
                throw new InvalidOperationException(Strings.Get("err.cf.nosecret"));
            await _cloudflare.UploadWorkerAsync(cfToken, account.Id, workerName, proxySecret, ct).ConfigureAwait(false);
            Complete(_deployWorker, workerName);

            current = Begin(_enableRoute);
            var routed = await _cloudflare.EnableSubdomainRouteAsync(cfToken, account.Id, workerName, ct).ConfigureAwait(false);
            var workerUrl = $"https://{workerName}.{subdomain}.workers.dev";
            if (routed)
                Complete(_enableRoute, workerUrl);
            else
                Warn(_enableRoute, Strings.Get("ui.step.routeunconfirmed"));

            current = Begin(_probeEdge);
            var probe = await _cloudflare.ProbeWorkerAsync(workerUrl, proxySecret, ct).ConfigureAwait(false);
            if (!probe.Reachable)
                // Propagation can lag; the bot check below is the real proof, so this never blocks.
                Warn(_probeEdge, Strings.Get("ui.step.propagating"));
            else if (probe.Version is { } deployed && deployed != CloudflareService.ExpectedWorkerVersion)
                // The upload above should have replaced it, so a mismatch here means the
                // edge is still serving a cached older script. Say so rather than let the
                // two drift silently.
                Warn(_probeEdge, Strings.Format("ui.step.workerstale", deployed, CloudflareService.ExpectedWorkerVersion));
            else
                Complete(_probeEdge, Strings.Get("ui.step.edgeanswering"));

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
            var withBot = _settings.Current.Clone();
            withBot.TelegramBotToken = botToken;
            withBot.TelegramBotUsername = me.Username ?? string.Empty;
            _settings.Save(withBot);
            Complete(_verifyBot, $"@{me.Username}");

            current = Begin(_startRelay);
            // The poll loop caches the URL, token and secret it started with, and
            // StartAsync is a no-op while it is already running. A re-run that changed
            // any of those has to replace the loop, or the pipeline would report a new
            // relay while the old one kept polling with the old credentials.
            if (_bot.IsRunning)
                await _bot.StopAsync().ConfigureAwait(false);
            await _bot.StartAsync().ConfigureAwait(false);
            Complete(_startRelay, Strings.Get("ui.step.listening"));

            _log.Info($"Relay is up: {workerUrl} as @{me.Username}.");
            return new ConnectionResult(true, workerUrl, me.Username, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (current is not null)
                Fail(current, Strings.Get("ui.step.cancelled"));
            return new ConnectionResult(false, null, null, Strings.Get("ui.connect.cancelled"));
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
