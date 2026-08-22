using SoulRemote.Services;
using SoulRemote.Services.Security;

namespace SoulRemote.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        LoadFromSettings();

        ConnectCloudflareCommand = new AsyncRelayCommand(ConnectCloudflareAsync, () => !IsBusy);
        TestTelegramCommand = new AsyncRelayCommand(TestTelegramAsync, () => !IsBusy && IsCloudflareConnected);
        SaveCommand = new RelayCommand(Save);
    }

    // ---- Cloudflare ----
    private string _cloudflareApiToken = string.Empty;
    public string CloudflareApiToken { get => _cloudflareApiToken; set => SetProperty(ref _cloudflareApiToken, value); }

    private string _workerName = "soul-remote-proxy";
    public string WorkerName { get => _workerName; set => SetProperty(ref _workerName, value); }

    private string _proxySecret = string.Empty;
    public string ProxySecret { get => _proxySecret; set => SetProperty(ref _proxySecret, value); }

    private string _workerUrl = string.Empty;
    public string WorkerUrl { get => _workerUrl; private set { if (SetProperty(ref _workerUrl, value)) OnPropertyChanged(nameof(IsCloudflareConnected)); } }

    public bool IsCloudflareConnected => !string.IsNullOrWhiteSpace(WorkerUrl);

    private string _cloudflareStatus = string.Empty;
    public string CloudflareStatus { get => _cloudflareStatus; private set => SetProperty(ref _cloudflareStatus, value); }

    // ---- Telegram ----
    private string _telegramBotToken = string.Empty;
    public string TelegramBotToken { get => _telegramBotToken; set => SetProperty(ref _telegramBotToken, value); }

    private string _telegramStatus = string.Empty;
    public string TelegramStatus { get => _telegramStatus; private set => SetProperty(ref _telegramStatus, value); }

    // ---- toggles ----
    private bool _allowShellCommands;
    public bool AllowShellCommands { get => _allowShellCommands; set => SetProperty(ref _allowShellCommands, value); }

    private bool _startWithWindows;
    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }

    private bool _autoStartBot;
    public bool AutoStartBot { get => _autoStartBot; set => SetProperty(ref _autoStartBot, value); }

    private bool _startMinimized;
    public bool StartMinimized { get => _startMinimized; set => SetProperty(ref _startMinimized, value); }

    private bool _notifyOnStartup = true;
    public bool NotifyOnStartup { get => _notifyOnStartup; set => SetProperty(ref _notifyOnStartup, value); }

    private int _pollTimeoutSeconds = 25;
    public int PollTimeoutSeconds { get => _pollTimeoutSeconds; set => SetProperty(ref _pollTimeoutSeconds, value); }

    // ---- ui state ----
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsNotBusy)); }
    }
    public bool IsNotBusy => !_isBusy;

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    // ---- commands ----
    public AsyncRelayCommand ConnectCloudflareCommand { get; }
    public AsyncRelayCommand TestTelegramCommand { get; }
    public RelayCommand SaveCommand { get; }

    private void LoadFromSettings()
    {
        var s = _services.Settings.Current;
        CloudflareApiToken = s.CloudflareApiToken;
        WorkerName = string.IsNullOrWhiteSpace(s.WorkerName) ? "soul-remote-proxy" : s.WorkerName;
        ProxySecret = s.ProxySecret;
        WorkerUrl = s.WorkerUrl;
        TelegramBotToken = s.TelegramBotToken;
        AllowShellCommands = s.AllowShellCommands;
        AutoStartBot = s.AutoStartBot;
        StartMinimized = s.StartMinimized;
        NotifyOnStartup = s.NotifyOnStartup;
        PollTimeoutSeconds = s.PollTimeoutSeconds <= 0 ? 25 : s.PollTimeoutSeconds;
        _startWithWindows = _services.Startup.IsEnabled();
        OnPropertyChanged(nameof(StartWithWindows));
    }

    private async Task ConnectCloudflareAsync()
    {
        if (string.IsNullOrWhiteSpace(CloudflareApiToken))
        {
            CloudflareStatus = "Enter a Cloudflare API token first.";
            return;
        }

        IsBusy = true;
        CloudflareStatus = "Connecting and deploying worker…";
        try
        {
            if (string.IsNullOrWhiteSpace(ProxySecret))
                ProxySecret = SecureRandom.Token(28);

            // Persist inputs before the network call so nothing is lost.
            var s = _services.Settings.Current;
            s.CloudflareApiToken = CloudflareApiToken.Trim();
            s.WorkerName = WorkerName.Trim();
            s.ProxySecret = ProxySecret;
            _services.Settings.Save(s);

            var result = await _services.Cloudflare.ConnectAndDeployAsync(
                CloudflareApiToken.Trim(), WorkerName.Trim(), ProxySecret,
                _services.Settings.Current.CloudflareAccountId);

            s = _services.Settings.Current;
            s.CloudflareAccountId = result.AccountId;
            s.CloudflareAccountName = result.AccountName;
            s.WorkersDevSubdomain = result.Subdomain;
            s.WorkerUrl = result.WorkerUrl;
            _services.Settings.Save(s);

            WorkerUrl = result.WorkerUrl;
            CloudflareStatus = $"✓ Connected to '{result.AccountName}'. Worker: {result.WorkerUrl}";
        }
        catch (Exception ex)
        {
            CloudflareStatus = "✗ " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestTelegramAsync()
    {
        if (string.IsNullOrWhiteSpace(TelegramBotToken))
        {
            TelegramStatus = "Enter the Telegram bot token first.";
            return;
        }
        if (!IsCloudflareConnected)
        {
            TelegramStatus = "Connect Cloudflare first.";
            return;
        }

        IsBusy = true;
        TelegramStatus = "Checking bot through the proxy…";
        try
        {
            // Save token first, then verify via getMe through the worker.
            var s = _services.Settings.Current;
            s.TelegramBotToken = TelegramBotToken.Trim();
            _services.Settings.Save(s);

            _services.Telegram.Configure(_services.Settings.Current.WorkerUrl,
                TelegramBotToken.Trim(), _services.Settings.Current.ProxySecret);
            var me = await _services.Telegram.GetMeAsync();

            s = _services.Settings.Current;
            s.TelegramBotUsername = me.Username ?? string.Empty;
            _services.Settings.Save(s);

            TelegramStatus = $"✓ Bot OK: @{me.Username} ({me.FirstName}).";
        }
        catch (Exception ex)
        {
            TelegramStatus = "✗ " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Save()
    {
        var s = _services.Settings.Current;
        s.CloudflareApiToken = CloudflareApiToken.Trim();
        s.WorkerName = WorkerName.Trim();
        s.ProxySecret = ProxySecret;
        s.WorkerUrl = WorkerUrl;
        s.TelegramBotToken = TelegramBotToken.Trim();
        s.AllowShellCommands = AllowShellCommands;
        s.AutoStartBot = AutoStartBot;
        s.StartMinimized = StartMinimized;
        s.NotifyOnStartup = NotifyOnStartup;
        s.PollTimeoutSeconds = Math.Clamp(PollTimeoutSeconds, 5, 50);
        s.StartWithWindows = StartWithWindows;
        _services.Settings.Save(s);

        _services.Startup.SetEnabled(StartWithWindows);

        StatusMessage = $"Settings saved at {DateTime.Now:HH:mm:ss}.";
    }
}
