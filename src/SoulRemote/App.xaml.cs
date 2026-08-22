using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SoulRemote.Services;
using SoulRemote.ViewModels;

namespace SoulRemote;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private AppServices? _services;
    private TrayIconManager? _tray;
    private MainViewModel? _mainViewModel;

    public static AppServices Services =>
        ((App)Current)._services ?? throw new InvalidOperationException("Services not initialized.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard.
        _singleInstanceMutex = new Mutex(true, "SoulRemote.SingleInstance.9F16", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Soul Remote is already running (check the system tray).",
                "Soul Remote", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Keep the app alive when the main window is hidden to the tray.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            _services?.Log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "Soul Remote — error",
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

        _mainViewModel = new MainViewModel(_services);

        var window = new MainWindow { DataContext = _mainViewModel };
        MainWindow = window;

        _tray = new TrayIconManager(window, _services, () => ExplicitShutdown());

        var startMinimized = _services.Settings.Current.StartMinimized ||
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

        // Auto-start the bot if configured and ready.
        if (_services.Settings.Current.AutoStartBot &&
            _services.Settings.Current.HasCloudflare &&
            _services.Settings.Current.HasTelegram)
        {
            _ = _mainViewModel.Dashboard.AutoStartAsync();
        }
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
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
