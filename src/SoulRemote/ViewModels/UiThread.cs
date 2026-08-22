using System.Windows;

namespace SoulRemote.ViewModels;

/// <summary>Marshals work onto the UI dispatcher without ever blocking the caller.</summary>
public static class UiThread
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else
            action();
    }
}
