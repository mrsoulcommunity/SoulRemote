using System.Reflection;
using SoulRemote.Services;

namespace SoulRemote.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public DashboardViewModel Dashboard { get; }
    public SettingsViewModel Settings { get; }
    public LogViewModel Log { get; }

    public string Title => "Soul Remote";
    public string Version => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    private int _selectedTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public MainViewModel(AppServices services)
    {
        Dashboard = new DashboardViewModel(services);
        Settings = new SettingsViewModel(services);
        Log = new LogViewModel(services);
    }
}
