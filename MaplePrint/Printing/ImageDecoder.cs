using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace MaplePrint.Printing;

/// <summary>
/// Turns a caller's payload into a bitmap we can draw. Accepts whatever GDI+
/// can read — PNG in practice, JPEG and BMP for free.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageDecoder
{
    /// <summary>
    /// A 576-dot receipt would have to be ~24 metres long to reach this. The cap
    /// exists because decoded size is not payload size: a few hundred kB of PNG
    /// can expand into gigabytes of pixels, and this process is meant to run for
    /// months on a counter PC.
    /// </summary>
    public const long MaxPixels = 40_000_000;

    public const int MaxDimension = 20_000;

    public static (Bitmap? Image, string? Error) Decode(byte[] payload)
    {
        if (payload.Length == 0) return (null, "Empty payload.");

        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);

            if (decoded.Width > MaxDimension || decoded.Height > MaxDimension)
                return (null, $"Image is {decoded.Width}x{decoded.Height}; the limit is {MaxDimension} in either direction.");

            var pixels = (long)decoded.Width * decoded.Height;
            if (pixels > MaxPixels)
                return (null, $"Image is {pixels:N0} pixels; the limit is {MaxPixels:N0}.");

            // Copied for two reasons. GDI+ keeps a reference to the source
            // stream for the image's lifetime, so returning the decoded object
            // would mean keeping the MemoryStream alive with it. And flattening
            // onto white resolves transparency here, rather than leaving a
            // thermal head to decide what an alpha channel means — which it
            // does badly, usually by printing it solid black.
            var copy = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);

            // The canvas must carry the artwork's own resolution, not the
            // screen's. A new Bitmap is stamped with whatever DPI GDI+ read off
            // the desktop at startup — 144 on a counter PC running at 150% —
            // and every drawing call below is then measured against that.
            copy.SetResolution(decoded.HorizontalResolution, decoded.VerticalResolution);

            using (var g = Graphics.FromImage(copy))
            {
                // Pixel for pixel, stated three ways, because the obvious call
                // for this is a trap: DrawImageUnscaled is not unscaled. It
                // forwards to the physical-size overload, which scales by
                // graphics-DPI over image-DPI — so a 96 dpi PNG landing on a
                // 144 dpi canvas was drawn at 150% and clipped to the top-left
                // corner. Perfectly on a 96 dpi PC, ruined on a scaled one, and
                // MaplePrint's own test print never came through here to show it.
                g.PageUnit = GraphicsUnit.Pixel;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;

                g.Clear(Color.White);
                g.DrawImage(decoded, new Rectangle(0, 0, decoded.Width, decoded.Height));
            }

            return (copy, null);
        }
        catch (ArgumentException)
        {
            // What GDI+ raises for "this isn't an image I recognise".
            return (null, "Payload was not a readable image (expected PNG, JPEG or BMP).");
        }
        catch (Exception ex)
        {
            return (null, $"Could not decode the image: {ex.Message}");
        }
    }
}
