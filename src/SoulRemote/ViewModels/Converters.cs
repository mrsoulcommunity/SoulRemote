using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SoulRemote.Models;
using SoulRemote.Services;

namespace SoulRemote.ViewModels;

/// <summary>Maps a <see cref="LinkState"/> to its palette hue.</summary>
public sealed class LinkStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is LinkState state
            ? state switch
            {
                LinkState.Online => "SignalBrush",
                LinkState.Working => "StandbyBrush",
                LinkState.Fault => "FaultBrush",
                _ => "InkFaintBrush",
            }
            : "InkFaintBrush";

        return Application.Current?.TryFindResource(key) as Brush
               ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True -> Visible, False -> Collapsed. Pass "Invert" to reverse.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Non-empty string -> Visible, otherwise Collapsed.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Inverts a boolean (used to disable controls while work is in flight).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;
}

/// <summary>Checked when the bound value equals the ConverterParameter (nav rail selection).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b && parameter is not null ? parameter : Binding.DoNothing;
}

/// <summary>Maps a pipeline <see cref="StepStatus"/> to its palette hue.</summary>
public sealed class StepStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is StepStatus status
            ? status switch
            {
                StepStatus.Done => "SignalBrush",
                StepStatus.Running => "StandbyBrush",
                StepStatus.Failed => "FaultBrush",
                StepStatus.Skipped => "StandbyBrush",
                _ => "InkFaintBrush",
            }
            : "InkFaintBrush";

        return Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>A compact status marker for each pipeline stage.</summary>
public sealed class StepStatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StepStatus status
            ? status switch
            {
                StepStatus.Done => "✓",
                StepStatus.Running => "•",
                StepStatus.Failed => "✕",
                StepStatus.Skipped => "!",
                _ => "·",
            }
            : "·";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
