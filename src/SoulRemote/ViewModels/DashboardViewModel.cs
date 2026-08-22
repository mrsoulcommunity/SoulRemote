using System.Windows;
using System.Windows.Media;
using SoulRemote.Services;
using SoulRemote.Services.Security;

namespace SoulRemote.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public DashboardViewModel(AppServices services)
    {
        _services = services;

        StartCommand = new AsyncRelayCommand(StartAsync, () => !_services.Bot.IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _services.Bot.IsRunning);
        RegeneratePairingCommand = new RelayCommand(() => GeneratePairingCode());
        CopyPairingCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(PairingCode); } catch { /* clipboard may be busy */ }
        });
        ClearAuthorizedCommand = new RelayCommand(ClearAuthorized);
        RefreshCommand = new RelayCommand(RefreshAll);

        _services.Bot.StateChanged += OnBotStateChanged;
        _services.Router.ChatAuthorized += OnChatAuthorized;

        GeneratePairingCode();
        RefreshAll();
    }

    // ---- status ----
    private string _statusText = "● Offline";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private System.Windows.Media.Brush _statusColor = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
    public System.Windows.Media.Brush StatusColor { get => _statusColor; private set => SetProperty(ref _statusColor, value); }

    private string _detail = string.Empty;
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public string MachineName => Environment.MachineName;

    private string _botUsername = "—";
    public string BotUsername { get => _botUsername; private set => SetProperty(ref _botUsername, value); }

    private string _workerUrl = "—";
    public string WorkerUrl { get => _workerUrl; private set => SetProperty(ref _workerUrl, value); }

    // ---- pairing ----
    private string _pairingCode = string.Empty;
    public string PairingCode { get => _pairingCode; private set => SetProperty(ref _pairingCode, value); }

    private string _authorizedSummary = string.Empty;
    public string AuthorizedSummary { get => _authorizedSummary; private set => SetProperty(ref _authorizedSummary, value); }

    // ---- commands ----
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand RegeneratePairingCommand { get; }
    public RelayCommand CopyPairingCommand { get; }
    public RelayCommand ClearAuthorizedCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public async Task AutoStartAsync()
    {
        try { await StartAsync(); }
        catch { /* surfaced in log/status */ }
    }

    private async Task StartAsync()
    {
        try
        {
            await _services.Bot.StartAsync();
        }
        catch (Exception ex)
        {
            Detail = ex.Message;
        }
    }

    private async Task StopAsync() => await _services.Bot.StopAsync();

    private void GeneratePairingCode()
    {
        PairingCode = SecureRandom.NumericCode(6);
        _services.Router.PairingCode = PairingCode;
    }

    private void ClearAuthorized()
    {
        var result = MessageBox.Show("Remove all authorized Telegram chats?",
            "Soul Remote", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        // Clone before mutating: the poll-loop thread reads the live AuthorizedChatIds list.
        var s = _services.Settings.Current.Clone();
        s.AuthorizedChatIds.Clear();
        _services.Settings.Save(s);
        RefreshAll();
    }

    private void RefreshAll()
    {
        var s = _services.Settings.Current;
        WorkerUrl = string.IsNullOrEmpty(s.WorkerUrl) ? "—" : s.WorkerUrl;
        BotUsername = string.IsNullOrEmpty(s.TelegramBotUsername) ? "—" : "@" + s.TelegramBotUsername;
        var ids = s.AuthorizedChatIds.ToArray(); // snapshot; list may be swapped from the poll thread
        AuthorizedSummary = ids.Length == 0
            ? "No chats linked yet — send the pairing code from Telegram."
            : $"{ids.Length} chat(s) linked: {string.Join(", ", ids)}";
        UpdateStatus();
    }

    private void OnChatAuthorized(long chatId) => RunOnUi(() =>
    {
        // The router consumes the code on a successful pair; issue a fresh one for the next chat.
        GeneratePairingCode();
        RefreshAll();
    });

    private void OnBotStateChanged() => RunOnUi(() =>
    {
        UpdateStatus();
        if (!string.IsNullOrEmpty(_services.Bot.BotUsername))
            BotUsername = "@" + _services.Bot.BotUsername;
    });

    private void UpdateStatus()
    {
        switch (_services.Bot.State)
        {
            case BotState.Running:
                StatusText = "● Online";
                StatusColor = MakeBrush(0x3D, 0xD6, 0x8C);
                Detail = _services.Bot.LastError is { Length: > 0 } err ? $"Warning: {err}" : "Listening for Telegram commands.";
                break;
            case BotState.Starting:
                StatusText = "● Starting…";
                StatusColor = MakeBrush(0xF5, 0xB9, 0x42);
                Detail = "Connecting through Cloudflare…";
                break;
            case BotState.Error:
                StatusText = "● Error";
                StatusColor = MakeBrush(0xFF, 0x6B, 0x6B);
                Detail = _services.Bot.LastError ?? "Unknown error.";
                break;
            default:
                StatusText = "● Offline";
                StatusColor = MakeBrush(0xFF, 0x6B, 0x6B);
                Detail = "Bot is stopped.";
                break;
        }
    }

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else
            action();
    }
}
