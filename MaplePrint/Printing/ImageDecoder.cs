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
            using (var g = Graphics.FromImage(copy))
            {
                g.Clear(Color.White);
                g.DrawImageUnscaled(decoded, 0, 0);
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
