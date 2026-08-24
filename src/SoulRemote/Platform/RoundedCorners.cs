using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SoulRemote.Platform;

/// <summary>
/// Windows 11 rounds every window it draws the chrome for. This app draws its own
/// (CaptionHeight="0"), which opts it out, so it has to ask DWM for the same treatment
/// by hand. The attribute is silently ignored on Windows 10 and older, where the
/// desktop has no rounded windows to match in the first place.
/// </summary>
internal static class RoundedCorners
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    /// <summary>The radius DWM uses for a normal window on Windows 11, in DIPs.</summary>
    internal const double Radius = 8;

    /// <summary>Rounded corners arrived in Windows 11, which reports itself as build 22000.</summary>
    internal static bool IsSupported { get; } = Environment.OSVersion.Version.Build >= 22000;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal static void Apply(Window window)
    {
        if (!IsSupported) return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var preference = DwmwcpRound;
        // Purely cosmetic: a refusal here costs the app nothing but square corners.
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }
}
