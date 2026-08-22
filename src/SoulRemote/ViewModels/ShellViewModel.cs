using System.Reflection;
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

    private string _statusText = "Offline";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private Models.LinkState _statusState = Models.LinkState.Idle;
    public Models.LinkState StatusState { get => _statusState; private set => SetProperty(ref _statusState, value); }

    public RelayCommand NavigateCommand { get; }

    public ShellViewModel(AppServices services)
    {
        _services = services;

        Dashboard = new DashboardViewModel(services);
        Connect = new ConnectViewModel(services);
        Settings = new SettingsViewModel(services);
        Logs = new LogViewModel(services);

        NavigateCommand = new RelayCommand(p => Navigate(p as string ?? "dashboard"));

        // Land on whichever page has work to do: the wizard until the relay is set up.
        var configured = _services.Settings.Current.HasCloudflare && _services.Settings.Current.HasTelegram;
        Navigate(configured ? "dashboard" : "connect");

        _services.Bot.StateChanged += OnBotStateChanged;
        UpdateStatus();
    }

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
        // Settings changed on another page (Reduce motion, for one) only reach the
        // dashboard when it is next shown.
        if (CurrentKey == "dashboard")
            Dashboard.Refresh();
    }

    private void OnBotStateChanged() => UiThread.Post(UpdateStatus);

    private void UpdateStatus()
    {
        switch (_services.Bot.State)
        {
            case BotState.Running:
                StatusText = "Relay online";
                StatusState = Models.LinkState.Online;
                break;
            case BotState.Starting:
                StatusText = "Connecting";
                StatusState = Models.LinkState.Working;
                break;
            case BotState.Error:
                StatusText = "Link fault";
                StatusState = Models.LinkState.Fault;
                break;
            default:
                StatusText = "Offline";
                StatusState = Models.LinkState.Idle;
                break;
        }
    }
}
