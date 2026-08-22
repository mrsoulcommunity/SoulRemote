using System.Collections.Specialized;
using System.Windows.Controls;
using SoulRemote.ViewModels;

namespace SoulRemote.Views;

public partial class LogView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null)
            _observed.CollectionChanged -= OnCollectionChanged;

        if (DataContext is LogViewModel vm)
        {
            _observed = vm.Entries;
            _observed.CollectionChanged += OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }
}
