using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SoulRemote.Platform;

/// <summary>
/// Windows maximises a resizable window to the work area inflated by the frame
/// thickness, on the assumption that the overhang is non-client border it may safely
/// hide off-screen. Under WindowChrome the whole window is client area, so that
/// overhang carries real UI off with it on every edge: the rail runs off the left,
/// the caption buttons off the right, the status card under the taskbar.
///
/// The usual remedy is to answer WM_GETMINMAXINFO with the work area, but the values
/// written there are recomputed by the OS afterwards on this path and never take. So
/// the overhang is measured instead and handed back as padding, which keeps the fix
/// inside WPF's own layout where nothing can quietly undo it.
/// </summary>
internal static class MaximiseBounds
{
    private const int MonitorDefaultToNearest = 0x0002;

    /// <summary>
    /// How far the window currently spills past its monitor's work area, in DIPs.
    /// Zero on every edge whenever the window is not maximised, or if Windows
    /// declines to say where the monitor is.
    /// </summary>
    internal static Thickness Overhang(Window window)
    {
        if (window.WindowState != WindowState.Maximized) return default;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return default;

        if (!GetWindowRect(handle, out var bounds)) return default;

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return default;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return default;

        // Both rectangles are device pixels; layout wants DIPs, and the two differ on
        // any display that is not at 100%.
        var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice;
        var scaleX = scale?.M11 is > 0 and var x ? x : 1d;
        var scaleY = scale?.M22 is > 0 and var y ? y : 1d;

        return new Thickness(
            Math.Max(0, info.Work.Left - bounds.Left) / scaleX,
            Math.Max(0, info.Work.Top - bounds.Top) / scaleY,
            Math.Max(0, bounds.Right - info.Work.Right) / scaleX,
            Math.Max(0, bounds.Bottom - info.Work.Bottom) / scaleY);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }
}
