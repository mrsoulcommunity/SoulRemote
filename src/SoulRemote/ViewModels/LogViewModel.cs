using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SoulRemote.Services;

namespace SoulRemote.ViewModels;

/// <summary>Live activity log with a severity filter.</summary>
public sealed class LogViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public ReadOnlyObservableCollection<LogEntry> Entries => _services.Log.Entries;

    /// <summary>Filtered projection bound by the view.</summary>
    public ICollectionView View { get; }

    public LogViewModel(AppServices services)
    {
        _services = services;
        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = Matches;

        ClearCommand = new RelayCommand(() => _services.Log.Clear());
        SetFilterCommand = new RelayCommand(p => Filter = p as string ?? "all");
    }

    private string _filter = "all";
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            View.Refresh();
        }
    }

    private bool Matches(object item)
    {
        if (item is not LogEntry entry)
            return false;
        return Filter switch
        {
            "problems" => entry.Level is LogLevel.Warning or LogLevel.Error,
            "errors" => entry.Level == LogLevel.Error,
            _ => true,
        };
    }

    public RelayCommand ClearCommand { get; }
    public RelayCommand SetFilterCommand { get; }
}
