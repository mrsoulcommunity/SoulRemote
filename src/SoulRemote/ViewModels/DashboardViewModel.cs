using System.Windows;
using SoulRemote.Models;
using SoulRemote.Services;
using SoulRemote.Services.Security;

namespace SoulRemote.ViewModels;

/// <summary>
/// The operations view: the live relay chain, who is allowed to drive it, and the
/// controls you reach for from the machine itself.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public DashboardViewModel(AppServices services)
    {
        _services = services;

        StartCommand = new AsyncRelayCommand(StartAsync, () => !_services.Bot.IsRunning);
        StopCommand = new AsyncRelayCommand(() => _services.Bot.StopAsync(), () => _services.Bot.IsRunning);
        NewCodeCommand = new RelayCommand(GeneratePairingCode);
        CopyCodeCommand = new RelayCommand(CopyPairingCode);
        RevokeAllCommand = new RelayCommand(RevokeAll);
        LockCommand = new RelayCommand(() => Run(() => _services.System.Lock()));
        TestMessageCommand = new AsyncRelayCommand(SendTestMessageAsync, () => _services.Bot.IsRunning);

        _services.Bot.StateChanged += OnBotStateChanged;
        _services.Router.ChatAuthorized += OnChatAuthorized;
        _services.Router.CommandHandled += OnCommandHandled;

        GeneratePairingCode();
        Refresh();
    }

    // ---- relay chain ----

    private LinkState _pcState = LinkState.Online;
    public LinkState PcState { get => _pcState; private set => SetProperty(ref _pcState, value); }

    private LinkState _edgeState = LinkState.Idle;
    public LinkState EdgeState { get => _edgeState; private set => SetProperty(ref _edgeState, value); }

    private LinkState _telegramState = LinkState.Idle;
    public LinkState TelegramState { get => _telegramState; private set => SetProperty(ref _telegramState, value); }

    private bool _edgeLive;
    public bool EdgeLive { get => _edgeLive; private set => SetProperty(ref _edgeLive, value); }

    private bool _telegramLive;
    public bool TelegramLive { get => _telegramLive; private set => SetProperty(ref _telegramLive, value); }

    public string PcDetail => Environment.MachineName;

    private string _edgeDetail = "not deployed";
    public string EdgeDetail { get => _edgeDetail; private set => SetProperty(ref _edgeDetail, value); }

    private string _telegramDetail = "no bot";
    public string TelegramDetail { get => _telegramDetail; private set => SetProperty(ref _telegramDetail, value); }

    public bool AnimationsEnabled => _services.Settings.Current.ReduceMotion == false;

    // ---- headline ----

    private string _headline = "Relay offline";
    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }

    private string _subhead = "Connect Cloudflare and Telegram to start.";
    public string Subhead { get => _subhead; private set => SetProperty(ref _subhead, value); }

    private LinkState _overall = LinkState.Idle;
    public LinkState Overall { get => _overall; private set => SetProperty(ref _overall, value); }

    // ---- telemetry ----

    private string _uptime = "—";
    public string Uptime { get => _uptime; private set => SetProperty(ref _uptime, value); }

    private string _bootedAt = string.Empty;
    public string BootedAt { get => _bootedAt; private set => SetProperty(ref _bootedAt, value); }

    private bool _isRelayRunning;
    /// <summary>Drives which of Start/Stop is offered, so the panel never shows the wrong one.</summary>
    public bool IsRelayRunning { get => _isRelayRunning; private set => SetProperty(ref _isRelayRunning, value); }

    private int _commandCount;
    public int CommandCount { get => _commandCount; private set => SetProperty(ref _commandCount, value); }

    private string _lastCommand = "—";
    public string LastCommand { get => _lastCommand; private set => SetProperty(ref _lastCommand, value); }

    private int _chatCount;
    public int ChatCount { get => _chatCount; private set => SetProperty(ref _chatCount, value); }

    private string _chatList = string.Empty;
    public string ChatList { get => _chatList; private set => SetProperty(ref _chatList, value); }

    // ---- pairing ----

    private string _pairingCode = string.Empty;
    public string PairingCode { get => _pairingCode; private set => SetProperty(ref _pairingCode, value); }

    private string _botHandle = "—";
    public string BotHandle { get => _botHandle; private set => SetProperty(ref _botHandle, value); }

    // ---- commands ----

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand NewCodeCommand { get; }
    public RelayCommand CopyCodeCommand { get; }
    public RelayCommand RevokeAllCommand { get; }
    public RelayCommand LockCommand { get; }
    public AsyncRelayCommand TestMessageCommand { get; }

    public async Task AutoStartAsync()
    {
        try { await _services.Bot.StartAsync(); }
        catch { /* surfaced through state + logs */ }
    }

    private async Task StartAsync()
    {
        try
        {
            await _services.Bot.StartAsync();
        }
        catch (Exception ex)
        {
            Subhead = ex.Message;
        }
    }

    private async Task SendTestMessageAsync()
    {
        var chats = _services.Settings.Current.AuthorizedChatIds.ToArray();
        if (chats.Length == 0)
        {
            Subhead = "Pair a Telegram chat first — send /pair with the code below.";
            return;
        }
        foreach (var chat in chats)
        {
            try
            {
                await _services.Telegram.SendMessageAsync(chat,
                    $"🛰 Test from <b>{TextUtil.Html(Environment.MachineName)}</b> — the relay is working.");
            }
            catch (Exception ex)
            {
                Subhead = $"Test message failed: {ex.Message}";
                return;
            }
        }
        Subhead = "Test message delivered.";
    }

    private void GeneratePairingCode()
    {
        PairingCode = SecureRandom.NumericCode(6);
        _services.Router.PairingCode = PairingCode;
    }

    private void CopyPairingCode()
    {
        try { Clipboard.SetText($"/pair {PairingCode}"); }
        catch { /* the clipboard can be locked by another app */ }
    }

    private void RevokeAll()
    {
        var answer = MessageBox.Show(
            "Revoke every paired Telegram chat? They will need a new pairing code to control this machine.",
            "Soul Remote", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var s = _services.Settings.Current.Clone();
        s.AuthorizedChatIds.Clear();
        _services.Settings.Save(s);
        Refresh();
    }

    private void Run(Func<string> action)
    {
        try { Subhead = action(); }
        catch (Exception ex) { Subhead = ex.Message; }
    }

    private void OnChatAuthorized(long chatId) => UiThread.Post(() =>
    {
        GeneratePairingCode();
        Refresh();
    });

    private void OnCommandHandled(string command) => UiThread.Post(() =>
    {
        CommandCount = _services.Router.CommandsHandled;
        LastCommand = command;
    });

    private void OnBotStateChanged() => UiThread.Post(Refresh);

    /// <summary>Re-reads settings and bot state. Called when the page is shown.</summary>
    public void Refresh()
    {
        var s = _services.Settings.Current;
        var running = _services.Bot.State == BotState.Running;
        var starting = _services.Bot.State == BotState.Starting;
        var fault = _services.Bot.State == BotState.Error;

        // Edge hop: deployed at all, and carrying traffic while the relay runs.
        EdgeState = !s.HasCloudflare ? LinkState.Idle
            : fault ? LinkState.Fault
            : starting ? LinkState.Working
            : running ? LinkState.Online
            : LinkState.Idle;

        TelegramState = !s.HasTelegram ? LinkState.Idle
            : fault ? LinkState.Fault
            : starting ? LinkState.Working
            : running ? LinkState.Online
            : LinkState.Idle;

        PcState = LinkState.Online;
        EdgeLive = running;
        TelegramLive = running;

        EdgeDetail = string.IsNullOrEmpty(s.WorkerUrl)
            ? "not deployed"
            : s.WorkerUrl.Replace("https://", string.Empty);
        TelegramDetail = string.IsNullOrEmpty(s.TelegramBotUsername) ? "no bot" : "@" + s.TelegramBotUsername;
        BotHandle = string.IsNullOrEmpty(s.TelegramBotUsername) ? "—" : "@" + s.TelegramBotUsername;

        var ids = s.AuthorizedChatIds.ToArray();
        ChatCount = ids.Length;
        ChatList = ids.Length == 0 ? "No chats paired yet" : string.Join("  ·  ", ids);

        CommandCount = _services.Router.CommandsHandled;
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Uptime = TextUtil.HumanDuration(up);
        BootedAt = "since " + DateTime.Now.Subtract(up).ToString("d MMM, HH:mm");
        IsRelayRunning = running;
        OnPropertyChanged(nameof(AnimationsEnabled));

        if (running)
        {
            Overall = LinkState.Online;
            Headline = "Relay online";
            Subhead = _services.Bot.LastError is { Length: > 0 } warn
                ? $"Listening, with a warning: {warn}"
                : "Listening for commands from your paired chats.";
        }
        else if (starting)
        {
            Overall = LinkState.Working;
            Headline = "Connecting";
            Subhead = "Bringing the relay up through Cloudflare…";
        }
        else if (fault)
        {
            Overall = LinkState.Fault;
            Headline = "Link fault";
            Subhead = _services.Bot.LastError ?? "The relay stopped unexpectedly.";
        }
        else
        {
            Overall = LinkState.Idle;
            Headline = "Relay offline";
            Subhead = s.HasCloudflare && s.HasTelegram
                ? "Press Start relay to begin listening."
                : "Connect Cloudflare and Telegram to start.";
        }
    }
}
