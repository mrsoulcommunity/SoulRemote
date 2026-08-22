using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace SoulRemote.Services;

public interface IScreenshotService
{
    /// <summary>Captures the full virtual desktop (all monitors) as PNG bytes.</summary>
    byte[] CaptureAll();

    /// <summary>Captures a single monitor (0-based) as PNG bytes.</summary>
    byte[] CaptureScreen(int index);

    int ScreenCount { get; }
}

public sealed class ScreenshotService : IScreenshotService
{
    public int ScreenCount => WinFormsScreen.AllScreens.Length;

    public byte[] CaptureAll()
    {
        // Bounding rectangle across every monitor (handles multi-monitor + negative origins).
        var screens = WinFormsScreen.AllScreens;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var s in screens)
        {
            left = Math.Min(left, s.Bounds.Left);
            top = Math.Min(top, s.Bounds.Top);
            right = Math.Max(right, s.Bounds.Right);
            bottom = Math.Max(bottom, s.Bounds.Bottom);
        }
        var bounds = Rectangle.FromLTRB(left, top, right, bottom);
        return Capture(bounds);
    }

    public byte[] CaptureScreen(int index)
    {
        var screens = WinFormsScreen.AllScreens;
        if (index < 0 || index >= screens.Length)
            throw new ArgumentOutOfRangeException(nameof(index), $"Screen {index} does not exist (found {screens.Length}).");
        return Capture(screens[index].Bounds);
    }

    private static byte[] Capture(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("Invalid screen bounds for capture.");

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
