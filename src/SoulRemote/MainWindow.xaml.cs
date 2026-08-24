using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoulRemote.Platform;

namespace SoulRemote;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RoundedCorners.Apply(this);
        SyncShell();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        SyncShell();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximised();
            return;
        }
        // DragMove throws if the button was released before the call lands.
        try { DragMove(); }
        catch (InvalidOperationException) { /* mouse already released */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximised();

    // Closing hides to the tray; the relay keeps running. Exit lives in the tray menu.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximised()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ShellContent_SizeChanged(object sender, SizeChangedEventArgs e) => ClipShellContent();

    // A maximise, a DPI change and a move to another monitor all land here, and each
    // of them can change both the corner radius and the overhang.
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        SyncShell();
    }

    /// <summary>
    /// Windows never rounds a maximised window, and neither should the shell: a radius
    /// there would open four notches onto the desktop in a surface that fills the screen.
    /// Maximised it instead takes the padding that keeps its edges on screen.
    /// </summary>
    private void SyncShell()
    {
        var maximised = WindowState == WindowState.Maximized;

        // Maximised there is no frame left to draw: the window fills the work area, and
        // the hairline would be riding the screen edge with nothing on the other side.
        Shell.BorderThickness = new Thickness(maximised ? 0 : 1);
        Shell.CornerRadius = new CornerRadius(
            RoundedCorners.IsSupported && !maximised ? RoundedCorners.Radius : 0d);
        Shell.Padding = MaximiseBounds.Overhang(this);

        ClipShellContent();
    }

    /// <summary>
    /// A Border's CornerRadius rounds its own background and hairline but not its child,
    /// whose square corners would paint straight over the arc. The clip is inset by the
    /// hairline so the border stays visible all the way round.
    /// </summary>
    private void ClipShellContent()
    {
        var radius = Shell.CornerRadius.TopLeft - Shell.BorderThickness.Left;
        if (radius <= 0)
        {
            ShellContent.Clip = null;
            return;
        }

        ShellContent.Clip = new RectangleGeometry(
            new Rect(0, 0, ShellContent.ActualWidth, ShellContent.ActualHeight), radius, radius);
    }
}
