using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using SoulRemote.Localization;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace SoulRemote.Services;

/// <summary>
/// Multi-monitor capture, shaped to what Telegram will actually accept.
///
/// A lossless PNG of a 4K desktop — let alone three of them side by side — is both
/// far over sendPhoto's 10 MB limit and over its 10,000-pixel width+height limit, so
/// the naive version of this failed on exactly the machines it was most useful on,
/// and was punishing to download on the throttled connections this app exists for.
/// So a capture is downscaled until it fits the pixel budget, and re-encoded as JPEG
/// if the PNG is still too heavy. Only if it cannot be made to fit at all does it go
/// out as a document instead of a photo, which has no such limits.
/// </summary>
public sealed class ScreenshotService : IScreenshotService
{
    /// <summary>Below this, JPEG artefacts start to eat small text.</summary>
    private const long JpegQuality = 82L;

    public int ScreenCount => WinFormsScreen.AllScreens.Length;

    public ScreenCapture CaptureAll(long maxPhotoBytes, int maxDimensionSum)
    {
        // Bounding rectangle across every monitor (handles multi-monitor + negative origins).
        var screens = WinFormsScreen.AllScreens;
        if (screens.Length == 0)
            throw new InvalidOperationException(Strings.Get("bot.capture.nodisplays"));

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var s in screens)
        {
            left = Math.Min(left, s.Bounds.Left);
            top = Math.Min(top, s.Bounds.Top);
            right = Math.Max(right, s.Bounds.Right);
            bottom = Math.Max(bottom, s.Bounds.Bottom);
        }
        return Capture(Rectangle.FromLTRB(left, top, right, bottom), "desktop", maxPhotoBytes, maxDimensionSum);
    }

    public ScreenCapture CaptureScreen(int index, long maxPhotoBytes, int maxDimensionSum)
    {
        var screens = WinFormsScreen.AllScreens;
        if (index < 0 || index >= screens.Length)
            throw new ArgumentOutOfRangeException(nameof(index), $"Screen {index} does not exist (found {screens.Length}).");
        return Capture(screens[index].Bounds, $"monitor{index + 1}", maxPhotoBytes, maxDimensionSum);
    }

    private static ScreenCapture Capture(Rectangle bounds, string label, long maxPhotoBytes, int maxDimensionSum)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException(Strings.Get("bot.capture.badbounds"));

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        using var raw = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(raw))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var sized = FitWithin(raw, maxDimensionSum);
        var withinPixels = sized.Width + sized.Height <= maxDimensionSum;

        var png = Encode(sized, ImageFormat.Png);
        if (withinPixels && png.LongLength <= maxPhotoBytes)
            return new ScreenCapture(png, $"screenshot_{label}_{stamp}.png", true);

        // Photographic re-encode: a desktop screenshot survives JPEG far better than it
        // survives being refused outright.
        var jpeg = EncodeJpeg(sized);
        if (withinPixels && jpeg.LongLength <= maxPhotoBytes)
            return new ScreenCapture(jpeg, $"screenshot_{label}_{stamp}.jpg", true);

        // Still too big, or too wide for sendPhoto at any weight: send it as a file.
        var payload = jpeg.LongLength < png.LongLength ? jpeg : png;
        var extension = ReferenceEquals(payload, jpeg) ? "jpg" : "png";
        return new ScreenCapture(payload, $"screenshot_{label}_{stamp}.{extension}", false);
    }

    /// <summary>
    /// Scales an image down until width+height is inside the budget, preserving aspect
    /// ratio. Returns the original when it already fits, so the common single-1080p
    /// case does no extra work.
    /// </summary>
    private static Bitmap FitWithin(Bitmap source, int maxDimensionSum)
    {
        if (maxDimensionSum <= 0 || source.Width + source.Height <= maxDimensionSum)
            return (Bitmap)source.Clone();

        var scale = (double)maxDimensionSum / (source.Width + source.Height);
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));

        var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(source, 0, 0, width, height);
        }
        return scaled;
    }

    private static byte[] Encode(Image image, ImageFormat format)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format);
        return ms.ToArray();
    }

    private static byte[] EncodeJpeg(Image image)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder is null)
            return Encode(image, ImageFormat.Jpeg);

        using var parameters = new EncoderParameters(1);
        using var quality = new EncoderParameter(Encoder.Quality, JpegQuality);
        parameters.Param[0] = quality;

        using var ms = new MemoryStream();
        image.Save(ms, encoder, parameters);
        return ms.ToArray();
    }
}
