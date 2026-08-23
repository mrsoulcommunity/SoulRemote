namespace SoulRemote.Abstractions;

/// <summary>
/// Marshals a callback onto whatever thread the user interface lives on.
///
/// The core has plenty of code that runs on the polling loop and needs to touch
/// something the UI is bound to. Rather than reach for WPF's dispatcher — which
/// would drag a Windows dependency through the whole library — it asks for one of
/// these. The desktop app supplies a dispatcher-backed one;
/// <see cref="ImmediateDispatcher"/> stands in everywhere else, including tests.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs the action on the UI thread without waiting for it.</summary>
    void Post(Action action);
}

/// <summary>Runs the action on the calling thread. The default outside the desktop app.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public static readonly ImmediateDispatcher Instance = new();

    public void Post(Action action) => action();
}
