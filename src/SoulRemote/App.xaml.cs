using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SoulRemote.Localization;
using SoulRemote.Services;
using SoulRemote.ViewModels;

namespace SoulRemote;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private AppServices? _services;
    private TrayIconManager? _tray;
    private ShellViewModel? _shell;

    public static AppServices Services =>
        ((App)Current)._services ?? throw new InvalidOperationException("Services not initialized.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard.
        _singleInstanceMutex = new Mutex(true, "SoulRemote.SingleInstance.9F16", out var isNew);
        if (!isNew)
        {
            MessageBox.Show(Strings.Get("ui.dialog.alreadyrunning"),
                Strings.Get("ui.dialog.title"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Keep the app alive when the main window is hidden to the tray.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            _services?.Log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, Strings.Get("ui.dialog.errortitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _services?.Log.Error("Unhandled domain exception", ex);
        };

        _services = new AppServices();
        _services.Log.Info("Soul Remote starting...");

        // Written by the copy that handed itself to the installer. Its presence is the
        // only way this process can tell an ordinary launch from the far side of an
        // update, and it carries the two things that would otherwise be lost: whether
        // the window was hidden, and whether the relay was up.
        var resumed = _services.Updates.TakePending();
        if (resumed is not null)
            _services.Log.Info(Strings.Format("ui.update.restarted", resumed.Version));

        _shell = new ShellViewModel(_services);

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;

        _tray = new TrayIconManager(window, _services, () => ExplicitShutdown());

        SessionEnding += OnSessionEnding;

        var startMinimized = _services.Settings.Current.StartMinimized ||
                             resumed?.Minimized == true ||
                             e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        if (startMinimized)
        {
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            // Do not call Show(); tray icon provides access.
        }
        else
        {
            window.Show();
        }

        // Auto-start the bot if configured and ready. An update counts as configured
        // whatever the setting says: the relay was carrying traffic a minute ago, and a
        // machine that came back from an update without it is a machine nobody can reach.
        if ((_services.Settings.Current.AutoStartBot || resumed?.RelayWasRunning == true) &&
            _services.Settings.Current.HasCloudflare &&
            _services.Settings.Current.HasTelegram)
        {
            _ = _shell.Dashboard.AutoStartAsync();
        }

        ConfigureUpdates(window);
    }

    /// <summary>
    /// Connects the updater to the three things only the app knows: where the window is,
    /// whether the relay is up, and how to close down so an installer can take over.
    /// </summary>
    private void ConfigureUpdates(MainWindow window)
    {
        var updates = _services!.UpdateCoordinator;

        // Read from the timer thread, so both hops go through the dispatcher. Invoke
        // rather than BeginInvoke because the answer is the point; there is no deadlock
        // to fear, since the UI thread is never waiting on the updater.
        updates.IsHiddenInTray = () => Dispatcher.Invoke(
            () => !window.IsVisible || window.WindowState == WindowState.Minimized);
        updates.IsRelayRunning = () => _services.Bot.IsRunning;
        updates.RequestShutdown = () => Dispatcher.BeginInvoke(() =>
        {
            _tray?.BeginExit();
            ExplicitShutdown();
        });

        // A card behind a hidden window tells nobody anything, so the tray says it too.
        updates.UpdateFound += release => Dispatcher.BeginInvoke(() =>
        {
            if (window.IsVisible)
                return;
            _tray?.Notify(Strings.Get("ui.dialog.title"),
                Strings.Format("ui.update.available", release.Version.ToString(3)));
        });

        updates.Start();
    }

    /// <summary>
    /// Windows is signing the user out or shutting down - or an installer's Restart
    /// Manager is asking the app to close so it can replace the executable it is
    /// running from.
    ///
    /// Closing the window normally hides to the tray, which is right for the close
    /// button and wrong here: refusing to go leaves the exe locked, and the installer's
    /// only remaining option is to tell the user to reboot. Settings are written
    /// atomically, so there is nothing to lose by leaving promptly.
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _services?.Log.Info($"Session ending ({e.ReasonSessionEnding}); shutting down.");
        _tray?.BeginExit();
        ExplicitShutdown();
    }

    public void ExplicitShutdown()
    {
        // Stop the bot WITHOUT blocking the UI thread: StopAsync marshals state/log
        // callbacks back onto the dispatcher, so a blocking .GetResult() here would
        // deadlock. Awaiting keeps the message loop pumping; we finish on the UI thread.
        _ = ShutdownGracefullyAsync();
    }

    private async Task ShutdownGracefullyAsync()
    {
        try
        {
            if (_services is not null)
                await _services.Bot.StopAsync();
        }
        catch (Exception ex)
        {
            _services?.Log.Error("Error while stopping the bot on exit", ex);
        }
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.UpdateCoordinator.Dispose();
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
