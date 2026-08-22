// This app enables BOTH <UseWPF> and <UseWindowsForms>. With ImplicitUsings on, the SDK
// imports System.Windows.Forms and System.Drawing globally, which makes several simple type
// names (Application, UserControl, MessageBox, Clipboard, Color, ...) ambiguous with their
// WPF equivalents. Soul Remote is a WPF app; the few WinForms/Drawing usages are confined to
// TrayIconManager, ScreenshotService and SystemInfoService and are fully qualified/aliased
// there. These aliases pin the ambiguous names to their WPF meaning project-wide.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Clipboard = System.Windows.Clipboard;
global using Color = System.Windows.Media.Color;
global using UserControl = System.Windows.Controls.UserControl;
