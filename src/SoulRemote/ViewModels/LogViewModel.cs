using System.Collections.ObjectModel;
using SoulRemote.Services;

namespace SoulRemote.ViewModels;

public sealed class LogViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public ReadOnlyObservableCollection<LogEntry> Entries => _services.Log.Entries;

    public RelayCommand ClearCommand { get; }

    public LogViewModel(AppServices services)
    {
        _services = services;
        ClearCommand = new RelayCommand(() => _services.Log.Clear());
    }
}
