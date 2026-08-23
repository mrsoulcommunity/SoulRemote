using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using SoulRemote.Localization;

namespace SoulRemote.Platform;

/// <summary>
/// Terse localized text in XAML: <c>Content="{p:T ui.dash.start}"</c>.
///
/// It hands back a binding onto <see cref="Loc"/> rather than a resolved string, so
/// every piece of text placed this way re-renders when the language changes instead
/// of keeping whatever it was built with. WPF's own <see cref="BindingBase"/> takes
/// care of the one awkward case — inside a Setter there is no dependency property to
/// bind against yet, and it returns the binding itself for the style to apply later.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>
/// Mirrors the current language's writing direction onto a window or panel:
/// <c>FlowDirection="{p:FlowDirection}"</c>. Persian is right-to-left, and setting
/// this at the root mirrors the whole layout — rail, columns, text alignment and
/// scrollbars — rather than leaving Persian text running the wrong way inside a
/// left-to-right frame.
/// </summary>
[MarkupExtensionReturnType(typeof(FlowDirection))]
public sealed class FlowDirectionExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(Loc.Direction))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = new FlowDirectionConverter(),
            FallbackValue = System.Windows.FlowDirection.LeftToRight,
        };
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class FlowDirectionConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => string.Equals(value as string, "RightToLeft", StringComparison.Ordinal)
                ? System.Windows.FlowDirection.RightToLeft
                : System.Windows.FlowDirection.LeftToRight;

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => Binding.DoNothing;
    }
}
