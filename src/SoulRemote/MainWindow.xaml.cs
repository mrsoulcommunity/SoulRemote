using System.Windows;
using System.Windows.Input;

namespace SoulRemote;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
}
