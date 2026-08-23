using System.Reflection;
using SoulRemote.Localization;
using SoulRemote.Services;

namespace SoulRemote.ViewModels;

/// <summary>
/// Owns navigation and the always-visible status strip in the rail. Each page is
/// a child view model; the window maps them to views with implicit DataTemplates.
/// </summary>
public sealed class ShellViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public DashboardViewModel Dashboard { get; }
    public ConnectViewModel Connect { get; }
    public SettingsViewModel Settings { get; }
    public LogViewModel Logs { get; }

    public string Version => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    public string MachineName => Environment.MachineName;

    private ViewModelBase _current = null!;
    public ViewModelBase Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    private string _currentKey = "dashboard";
    public string CurrentKey
    {
        get => _currentKey;
        private set => SetProperty(ref _currentKey, value);
    }

    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private Models.LinkState _statusState = Models.LinkState.Idle;
    public Models.LinkState StatusState { get => _statusState; private set => SetProperty(ref _statusState, value); }

    public RelayCommand NavigateCommand { get; }

    // Quick controls live in the rail so the most-used local actions are one click
    // from any page. All three are unambiguous power actions on this machine.
    public RelayCommand LockCommand { get; }
    public RelayCommand SleepCommand { get; }
    public RelayCommand DisplayOffCommand { get; }

    private string _quickResult = string.Empty;
    public string QuickResult { get => _quickResult; private set => SetProperty(ref _quickResult, value); }

    /// <summary>Page name shown in the title bar, which is otherwise dead space.</summary>
    public string Crumb => Strings.Get(CurrentKey switch
    {
        "connect" => "ui.crumb.connect",
        "settings" => "ui.crumb.settings",
        "logs" => "ui.crumb.logs",
        _ => "ui.crumb.dashboard",
    });

    public ShellViewModel(AppServices services)
    {
        _services = services;

        Dashboard = new DashboardViewModel(services);
        Connect = new ConnectViewModel(services);
        Settings = new SettingsViewModel(services);
        Logs = new LogViewModel(services);

        NavigateCommand = new RelayCommand(p => Navigate(p as string ?? "dashboard"));
        LockCommand = new RelayCommand(() => RunQuick(() => _services.System.Lock()));
        SleepCommand = new RelayCommand(() => RunQuick(() => _services.System.Sleep()));
        DisplayOffCommand = new RelayCommand(() => RunQuick(() => _services.System.MonitorOff()));

        // Land on whichever page has work to do: the wizard until the relay is set up.
        var configured = _services.Settings.Current.HasCloudflare && _services.Settings.Current.HasTelegram;
        Navigate(configured ? "dashboard" : "connect");

        _services.Bot.StateChanged += OnBotStateChanged;

        // A language change reaches here from two directions — the Settings page and
        // the button in the Telegram menu — so the shell listens for the change itself
        // rather than either caller having to remember to tell it.
        Strings.LanguageChanged += OnLanguageChanged;
        _services.Router.LanguageChanged += OnRouterLanguageChanged;

        UpdateStatus();
    }

    private void OnRouterLanguageChanged(AppLanguage language) => UiThread.Post(() =>
    {
        Strings.Use(language);
        Settings.NotifyLanguageChanged();
    });

    private void OnLanguageChanged() => UiThread.Post(() =>
    {
        // Loc drives every {p:T} binding in the window; these three are computed in
        // C# and would otherwise keep the wording they were built with.
        Localization.Loc.Instance.Refresh();
        _services.Orchestrator.RefreshLanguage();
        OnPropertyChanged(nameof(Crumb));
        UpdateStatus();
        Dashboard.Refresh();
    });

    public void Navigate(string key)
    {
        Current = key switch
        {
            "connect" => Connect,
            "settings" => Settings,
            "logs" => Logs,
            _ => Dashboard,
        };
        CurrentKey = key is "connect" or "settings" or "logs" ? key : "dashboard";
        OnPropertyChanged(nameof(Crumb));
        // Settings changed on another page (Reduce motion, for one) only reach the
        // dashboard when it is next shown.
        if (CurrentKey == "dashboard")
            Dashboard.Refresh();
    }

    private void RunQuick(Func<string> action)
    {
        try { QuickResult = action(); }
        catch (Exception ex) { QuickResult = ex.Message; }
    }

    private void OnBotStateChanged() => UiThread.Post(UpdateStatus);

    private void UpdateStatus()
    {
        switch (_services.Bot.State)
        {
            case BotState.Running:
                StatusText = Strings.Get("ui.status.online");
                StatusState = Models.LinkState.Online;
                break;
            case BotState.Starting:
                StatusText = Strings.Get("ui.status.connecting");
                StatusState = Models.LinkState.Working;
                break;
            case BotState.Error:
                StatusText = Strings.Get("ui.status.fault");
                StatusState = Models.LinkState.Fault;
                break;
            default:
                StatusText = Strings.Get("ui.status.offline");
                StatusState = Models.LinkState.Idle;
                break;
        }
    }
}
