using System.Collections.Specialized;
using System.Windows;
using SoulRemote.ViewModels;

namespace SoulRemote.Views;

public partial class LogView
{
    private INotifyCollectionChanged? _observed;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (DataContext is LogViewModel vm)
        {
            _observed = vm.Entries;
            _observed.CollectionChanged += OnCollectionChanged;
        }
    }

    private void Detach()
    {
        if (_observed is null)
            return;
        _observed.CollectionChanged -= OnCollectionChanged;
        _observed = null;
    }

    // Keep the newest line in view, but only while the user is already at the bottom.
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
            return;
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }
}
