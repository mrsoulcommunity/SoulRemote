using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using SoulRemote.Localization;
using WpfWindow = System.Windows.Window;

namespace SoulRemote.Services;

/// <summary>
/// Manages the system-tray presence so Soul Remote can keep the bot running in
/// the background while the main window is hidden.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly WpfWindow _window;
    private readonly AppServices _services;
    private readonly Action _exitAction;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _exitItem;
    private bool _exiting;
    private bool _balloonShown;
    private bool _disposed;

    public TrayIconManager(WpfWindow window, AppServices services, Action exitAction)
    {
        _window = window;
        _services = services;
        _exitAction = exitAction;

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Soul Remote",
        };

        _menu = new ContextMenuStrip();
        _openItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowWindow());
        _toggleItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await ToggleBotAsync());
        _exitItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ExitApp());
        _menu.Items.Add(_openItem);
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);
        _notifyIcon.ContextMenuStrip = _menu;

        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        _window.StateChanged += OnStateChanged;
        _window.Closing += OnClosing;
        _services.Bot.StateChanged += OnBotStateChanged;
        Strings.LanguageChanged += OnLanguageChanged;

        ApplyLanguage();
    }

    /// <summary>
    /// The tray icon is the app's own mark, read from the WPF resource rather than
    /// the executable, so it works the same in a single-file build where the exe on
    /// disk is a bundle. A missing or unreadable resource falls back to the generic
    /// Windows icon: an app that shows the wrong icon is still usable, one that
    /// throws on startup is not.
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            return stream is null ? SystemIcons.Application : new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception)
        {
            return SystemIcons.Application;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Minimized)
            HideToTray();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;
        // Closing the window hides to tray instead of exiting; the bot keeps running.
        e.Cancel = true;
        HideToTray();
    }

    private void OnBotStateChanged()
    {
        // Non-blocking marshal so a background StateChanged never waits on the UI thread.
        _window.Dispatcher.BeginInvoke(() =>
        {
            UpdateToggleText();
            _notifyIcon.Text = Strings.Get(_services.Bot.IsRunning ? "ui.tray.online" : "ui.tray.offline");
        });
    }

    private void UpdateToggleText()
        => _toggleItem.Text = Strings.Get(_services.Bot.IsRunning ? "ui.tray.stopbot" : "ui.tray.startbot");

    private void OnLanguageChanged() => _window.Dispatcher.BeginInvoke(ApplyLanguage);

    /// <summary>The tray menu is built once, so its captions are re-read on a switch.</summary>
    private void ApplyLanguage()
    {
        _openItem.Text = Strings.Get("ui.tray.open");
        _exitItem.Text = Strings.Get("ui.tray.exit");
        UpdateToggleText();
        _notifyIcon.Text = Strings.Get(_services.Bot.IsRunning ? "ui.tray.online" : "ui.tray.offline");
    }

    private async Task ToggleBotAsync()
    {
        try
        {
            if (_services.Bot.IsRunning)
                await _services.Bot.StopAsync();
            else
                await _services.Bot.StartAsync();
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(4000, Strings.Get("ui.dialog.title"), ex.Message, ToolTipIcon.Error);
        }
    }

    private void HideToTray()
    {
        _window.Hide();
        _window.ShowInTaskbar = false;
        if (!_balloonShown)
        {
            _balloonShown = true;
            _notifyIcon.ShowBalloonTip(3000, Strings.Get("ui.dialog.title"),
                Strings.Get("ui.tray.hidden"), ToolTipIcon.Info);
        }
    }

    private void ShowWindow()
    {
        // Order matters: on the start-minimized path the window has never been shown,
        // so calling Show() while WindowState is still Minimized raises StateChanged
        // and sends it straight back to the tray. Restore first, then show.
        _window.ShowInTaskbar = true;
        _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void ExitApp()
    {
        BeginExit();
        _exitAction();
    }

    /// <summary>
    /// Marks the app as on its way out, so the window's Closing handler stops sending
    /// it back to the tray. Anything that must really exit - the tray's Exit item, and
    /// the session-ending path an installer's Restart Manager goes through - calls
    /// this first, or the close is quietly cancelled and the process stays alive.
    /// </summary>
    public void BeginExit() => _exiting = true;

    /// <summary>
    /// A balloon from the tray. Used when something worth knowing happens while the
    /// window is hidden - a new version, for one - because a card behind a hidden window
    /// tells nobody anything. Clicking the balloon brings the window up, so the offer is
    /// one click away rather than buried in a menu.
    /// </summary>
    public void Notify(string title, string message)
    {
        void OnClicked(object? sender, EventArgs e)
        {
            _notifyIcon.BalloonTipClicked -= OnClicked;
            ShowWindow();
        }

        _notifyIcon.BalloonTipClicked += OnClicked;
        _notifyIcon.ShowBalloonTip(6000, title, message, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _services.Bot.StateChanged -= OnBotStateChanged;
        Strings.LanguageChanged -= OnLanguageChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
