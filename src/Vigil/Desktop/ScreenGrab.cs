using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Forms = System.Windows.Forms;

namespace Vigil.Desktop;

/// <summary>Taking a picture of the screen, and making it small enough to send.</summary>
internal static class ScreenGrab
{
    public const int MaxWidth = 1400;
    const long JpegQuality = 72;

    /// <summary>Monitor 1 is the primary one; the rest follow in Windows' order.</summary>
    public static Rectangle Bounds(int monitor)
    {
        var screens = Forms.Screen.AllScreens.OrderByDescending(s => s.Primary).ToArray();
        var index = monitor < 1 || monitor > screens.Length ? 0 : monitor - 1;
        return screens[index].Bounds;
    }

    public static int MonitorCount => Forms.Screen.AllScreens.Length;

    public static Bitmap Grab(Rectangle area)
    {
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>A JPEG no wider than MaxWidth. Returns the scale from the image back to the screen.</summary>
    public static (byte[] Jpeg, double Scale) Encode(Bitmap image, int maxWidth = MaxWidth)
    {
        var scale = 1.0;
        Bitmap? resized = null;
        if (image.Width > maxWidth)
        {
            scale = image.Width / (double)maxWidth;
            resized = new Bitmap(maxWidth, (int)Math.Round(image.Height / scale), PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(resized);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(image, 0, 0, resized.Width, resized.Height);
        }

        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        (resized ?? image).Save(stream, codec, parameters);
        resized?.Dispose();
        return (stream.ToArray(), scale);
    }
}
