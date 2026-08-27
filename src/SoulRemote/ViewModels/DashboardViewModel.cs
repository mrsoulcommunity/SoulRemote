using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using SoulRemote.Localization;
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
        // A chat can now be renamed or revoked from Telegram, so this list has another
        // writer. Refresh() only ran on navigation and bot-state changes, which left
        // the rows showing a chat that had already lost access.
        _services.Settings.Changed += _ => UiThread.Post(Refresh);
        _services.Router.CommandHandled += OnCommandHandled;

        // Uptime is the one tile that should visibly move. It was only recomputed on a
        // bot state change, and a healthy relay raises none — so on the page you land
        // on, a counter reading minutes and seconds sat still.
        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _tick.Tick += (_, _) => RefreshUptime();
        _tick.Start();

        GeneratePairingCode();
        Refresh();
    }

    private readonly DispatcherTimer _tick;

    private void RefreshUptime()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Uptime = TextUtil.HumanDuration(up);
        BootedAt = Strings.Format("ui.dash.since", DateTime.Now.Subtract(up).ToString("d MMM, HH:mm"));
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

    private string _edgeDetail = string.Empty;
    public string EdgeDetail { get => _edgeDetail; private set => SetProperty(ref _edgeDetail, value); }

    private string _telegramDetail = string.Empty;
    public string TelegramDetail { get => _telegramDetail; private set => SetProperty(ref _telegramDetail, value); }

    public bool AnimationsEnabled => _services.Settings.Current.ReduceMotion == false;

    // ---- headline ----

    private string _headline = string.Empty;
    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }

    private string _subhead = string.Empty;
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

    /// <summary>Each paired chat as its own row, so one can be revoked without the rest.</summary>
    public ObservableCollection<PairedChatViewModel> PairedChats { get; } = new();

    public bool HasPairedChats => PairedChats.Count > 0;

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
            Subhead = Strings.Get("ui.dash.test.nochats");
            return;
        }
        foreach (var chat in chats)
        {
            try
            {
                await _services.Telegram.SendMessageAsync(chat,
                    Strings.Format("bot.test", TextUtil.Html(Environment.MachineName)));
            }
            catch (Exception ex)
            {
                Subhead = Strings.Format("ui.dash.test.failed", ex.Message);
                return;
            }
        }
        Subhead = Strings.Get("ui.dash.test.sent");
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
            Strings.Get("ui.dash.revoke.confirm"), Strings.Get("ui.dialog.title"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var s = _services.Settings.Current.Clone();
        foreach (var id in s.AuthorizedChatIds)
            _services.Router.Forget(id);
        s.AuthorizedChatIds.Clear();
        s.ChatNames.Clear();
        // "I cut those devices off" must not be reported when the write did not land:
        // Refresh() would simply redraw the rows and leave the chats authorized.
        if (!_services.Settings.Save(s))
            Subhead = Strings.Get("ui.settings.savefailed");
        Refresh();
    }

    private void RevokeOne(long chatId)
    {
        var current = _services.Settings.Current;
        var answer = MessageBox.Show(
            Strings.Format("ui.dash.remove.confirm", current.NameFor(chatId)), Strings.Get("ui.dialog.title"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var s = current.Clone();
        s.AuthorizedChatIds.Remove(chatId);
        if (!_services.Settings.Save(s))
            Subhead = Strings.Get("ui.settings.savefailed");
        // Drop anything the router still holds for that chat: a half-finished prompt
        // or a folder listing must not survive the chat losing access.
        _services.Router.Forget(chatId);
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
            ? Strings.Get("ui.dash.notdeployed")
            : s.WorkerUrl.Replace("https://", string.Empty);
        TelegramDetail = string.IsNullOrEmpty(s.TelegramBotUsername)
            ? Strings.Get("ui.dash.nobot") : "@" + s.TelegramBotUsername;
        BotHandle = string.IsNullOrEmpty(s.TelegramBotUsername) ? "—" : "@" + s.TelegramBotUsername;

        var ids = s.AuthorizedChatIds.ToArray();
        ChatCount = ids.Length;
        ChatList = ids.Length == 0 ? Strings.Get("ui.dash.nochats") : string.Join("  ·  ", ids);

        PairedChats.Clear();
        foreach (var id in ids)
            PairedChats.Add(new PairedChatViewModel(id, s.NameFor(id), RevokeOne));
        OnPropertyChanged(nameof(HasPairedChats));

        CommandCount = _services.Router.CommandsHandled;
        RefreshUptime();
        IsRelayRunning = running;
        OnPropertyChanged(nameof(AnimationsEnabled));

        if (running)
        {
            Overall = LinkState.Online;
            Headline = Strings.Get("ui.status.online");
            Subhead = _services.Bot.LastError is { Length: > 0 } warn
                ? Strings.Format("ui.dash.sub.warning", warn)
                : Strings.Get("ui.dash.sub.listening");
        }
        else if (starting)
        {
            Overall = LinkState.Working;
            Headline = Strings.Get("ui.status.connecting");
            Subhead = Strings.Get("ui.dash.sub.connecting");
        }
        else if (fault)
        {
            Overall = LinkState.Fault;
            Headline = Strings.Get("ui.status.fault");
            Subhead = _services.Bot.LastError ?? Strings.Get("ui.dash.sub.fault");
        }
        else
        {
            Overall = LinkState.Idle;
            Headline = Strings.Get("ui.status.offline");
            Subhead = Strings.Get(s.HasCloudflare && s.HasTelegram
                ? "ui.dash.sub.ready"
                : "ui.dash.sub.notready");
        }
    }
}
