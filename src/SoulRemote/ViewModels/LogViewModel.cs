using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using SoulRemote.Localization;
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
        ((INotifyCollectionChanged)Entries).CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        ClearCommand = new RelayCommand(Clear);
        SetFilterCommand = new RelayCommand(p => Filter = p as string ?? "all");
    }

    /// <summary>Drives the empty-state message; an unexplained blank page reads as broken.</summary>
    public bool IsEmpty => View.IsEmpty;

    private string _filter = "all";
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            View.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
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

    /// <summary>
    /// Clears the list and the files behind it. The confirmation is there because the
    /// files are the only durable record of what the relay did, and deleting them is
    /// not undoable — but the list alone would be a false promise of privacy.
    /// </summary>
    private void Clear()
    {
        var answer = MessageBox.Show(
            Strings.Get("ui.logs.clear.confirm"), Strings.Get("ui.dialog.title"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        _services.Log.Clear();
        _services.Log.DeleteAllFiles();
    }

    public RelayCommand ClearCommand { get; }
    public RelayCommand SetFilterCommand { get; }
}
