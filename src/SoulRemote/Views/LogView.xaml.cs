using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoulRemote.ViewModels;

namespace SoulRemote.Views;

public partial class LogView
{
    private INotifyCollectionChanged? _observed;

    public LogView()
    {
        InitializeComponent();
        // DataContextChanged and Loaded are not paired with Unloaded, so both attach:
        // a view re-attached to the visual tree keeps auto-scrolling.
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
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

    /// <summary>
    /// Follows the newest line, but only while the user is already at the bottom —
    /// otherwise reading an earlier error would be impossible with the relay running.
    /// </summary>
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
            return;

        var scroller = FindScrollViewer(LogList);
        if (scroller is not null && scroller.VerticalOffset < scroller.ScrollableHeight - 1.0)
            return;

        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }
}
