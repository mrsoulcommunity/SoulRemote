using System.Windows;
using SoulRemote.Abstractions;

namespace SoulRemote.Platform;

/// <summary>
/// Posts work to the WPF dispatcher without ever blocking the caller.
///
/// Non-blocking is the whole point: the poll loop and the connection pipeline both
/// raise change notifications from background threads, and a blocking Invoke from
/// there deadlocks the moment the UI thread is itself waiting on that work — which
/// is exactly what happens during shutdown.
/// </summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    public static readonly WpfDispatcher Instance = new();

    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else
            action();
    }
}
