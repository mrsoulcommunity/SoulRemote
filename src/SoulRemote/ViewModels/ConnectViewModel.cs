using System.Windows;
using SoulRemote.Models;
using SoulRemote.Services;

namespace SoulRemote.ViewModels;

/// <summary>
/// The bring-up page: paste two tokens, press Connect once, and the orchestrator
/// deploys the relay worker, links Cloudflare, authenticates the bot and starts
/// listening — reporting every stage as it goes.
/// </summary>
public sealed class ConnectViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public ConnectViewModel(AppServices services)
    {
        _services = services;

        var s = _services.Settings.Current;
        _cloudflareToken = s.CloudflareApiToken;
        _telegramToken = s.TelegramBotToken;
        _workerName = string.IsNullOrWhiteSpace(s.WorkerName) ? "soul-remote-proxy" : s.WorkerName;
        _workerUrl = s.WorkerUrl;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsBusy && CanConnect);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        OpenCloudflareTokensCommand = new RelayCommand(() =>
            OpenUrl("https://dash.cloudflare.com/profile/api-tokens"));
        OpenBotFatherCommand = new RelayCommand(() => OpenUrl("https://t.me/BotFather"));

        _services.Orchestrator.Changed += OnOrchestratorChanged;
    }

    public IReadOnlyList<ConnectionStep> Steps => _services.Orchestrator.Steps;

    // ---- inputs ----

    private string _cloudflareToken;
    public string CloudflareToken
    {
        get => _cloudflareToken;
        set { if (SetProperty(ref _cloudflareToken, value)) OnPropertyChanged(nameof(CanConnect)); }
    }

    private string _telegramToken;
    public string TelegramToken
    {
        get => _telegramToken;
        set { if (SetProperty(ref _telegramToken, value)) OnPropertyChanged(nameof(CanConnect)); }
    }

    private string _workerName;
    public string WorkerName { get => _workerName; set => SetProperty(ref _workerName, value); }

    public bool CanConnect =>
        !string.IsNullOrWhiteSpace(CloudflareToken) && !string.IsNullOrWhiteSpace(TelegramToken);

    // ---- outcome ----

    private string _workerUrl;
    public string WorkerUrl { get => _workerUrl; private set => SetProperty(ref _workerUrl, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_isBusy;

    private string _message = string.Empty;
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    private LinkState _outcome = LinkState.Idle;
    public LinkState Outcome { get => _outcome; private set => SetProperty(ref _outcome, value); }

    // ---- commands ----

    public AsyncRelayCommand ConnectCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenCloudflareTokensCommand { get; }
    public RelayCommand OpenBotFatherCommand { get; }

    private async Task ConnectAsync()
    {
        IsBusy = true;
        Outcome = LinkState.Working;
        Message = "Bringing the relay up…";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var result = await _services.Orchestrator.RunAsync(
                new ConnectionRequest(CloudflareToken, WorkerName, TelegramToken), _cts.Token);

            if (result.Success)
            {
                WorkerUrl = result.WorkerUrl ?? string.Empty;
                Outcome = LinkState.Online;
                Message = $"Connected as @{result.BotUsername}. Open Telegram and send /pair with the code on the dashboard.";
            }
            else
            {
                Outcome = LinkState.Fault;
                Message = result.Error ?? "Connection failed.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnOrchestratorChanged() => UiThread.Post(() => OnPropertyChanged(nameof(Steps)));

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the browser: {ex.Message}", "Soul Remote",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
