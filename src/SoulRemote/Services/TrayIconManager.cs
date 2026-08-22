using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
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
    private readonly ToolStripMenuItem _toggleItem;
    private bool _exiting;
    private bool _balloonShown;

    public TrayIconManager(WpfWindow window, AppServices services, Action exitAction)
    {
        _window = window;
        _services = services;
        _exitAction = exitAction;

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Soul Remote",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open", null, (_, _) => ShowWindow()));
        _toggleItem = new ToolStripMenuItem("Start bot", null, async (_, _) => await ToggleBotAsync());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        _window.StateChanged += OnStateChanged;
        _window.Closing += OnClosing;
        _services.Bot.StateChanged += OnBotStateChanged;

        UpdateToggleText();
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
        _window.Dispatcher.Invoke(() =>
        {
            UpdateToggleText();
            _notifyIcon.Text = _services.Bot.IsRunning
                ? "Soul Remote — online"
                : "Soul Remote — offline";
        });
    }

    private void UpdateToggleText()
        => _toggleItem.Text = _services.Bot.IsRunning ? "Stop bot" : "Start bot";

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
            _notifyIcon.ShowBalloonTip(4000, "Soul Remote", ex.Message, ToolTipIcon.Error);
        }
    }

    private void HideToTray()
    {
        _window.Hide();
        _window.ShowInTaskbar = false;
        if (!_balloonShown)
        {
            _balloonShown = true;
            _notifyIcon.ShowBalloonTip(3000, "Soul Remote",
                "Still running in the tray. Right-click the icon for options.", ToolTipIcon.Info);
        }
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.ShowInTaskbar = true;
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void ExitApp()
    {
        _exiting = true;
        _exitAction();
    }

    public void Dispose()
    {
        _services.Bot.StateChanged -= OnBotStateChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
