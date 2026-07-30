using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace MaplePrint.Printing;

/// <summary>
/// Draws a bitmap through a printer's Windows driver.
///
/// This is the path for every printer that has no command language of its own —
/// lasers, inkjets, and raster receipt printers like the Star TSP100 family,
/// which accept nothing but what their driver produces. The caller renders the
/// page; MaplePrint only conveys it.
///
/// The whole difficulty here is making one bitmap pixel land on exactly one
/// printer dot. Get it wrong and a 203 dpi thermal printer produces soft,
/// resampled text that looks like a bad fax — the classic symptom of a receipt
/// drawn in the wrong units.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DocumentPrinter
{
    /// <summary>
    /// Assumed when a caller doesn't say what the artwork was drawn at. 203 dpi
    /// is the thermal standard, and what both the Star and the Zebra run at.
    /// </summary>
    public const int DefaultRenderDpi = 203;

    /// <summary>
    /// Prints <paramref name="image"/> to <paramref name="queueName"/>. Returns
    /// null on success, else a human-readable reason.
    /// </summary>
    /// <param name="paperSizeName">
    /// A form on the driver, e.g. <c>72mm x Receipt</c>. When null, a custom page
    /// exactly the size of the bitmap is used — which is what a variable-length
    /// receipt wants. The override exists because some drivers ignore custom
    /// sizes, and a site shouldn't need a new build to work around one.
    /// </param>
    /// <param name="note">Receives remarks worth logging that aren't failures.</param>
    /// <param name="outputFile">Diagnostics: print to this file instead of paper, where the driver supports it.</param>
    /// <param name="renderDpi">
    /// The resolution the artwork was drawn at, which decides its physical size.
    /// Not the device's resolution — see <c>Destination.RenderDpi</c>.
    /// </param>
    public static string? Print(
        string queueName,
        Bitmap image,
        string documentName,
        int copies = 1,
        string? paperSizeName = null,
        int renderDpi = DefaultRenderDpi,
        Action<string>? note = null,
        string? outputFile = null)
    {
        string? error = null;
        if (renderDpi <= 0) renderDpi = DefaultRenderDpi;

        // System.Drawing.Printing wants an STA thread; the caller reaches us
        // from a Kestrel request thread, which is anything but.
        var thread = new Thread(() =>
        {
            try { error = PrintCore(queueName, image, documentName, copies, paperSizeName, renderDpi, note, outputFile); }
            catch (Exception ex) { error = $"Printer '{queueName}': {ex.Message}"; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        return error;
    }

    private static string? PrintCore(
        string queueName,
        Bitmap image,
        string documentName,
        int copies,
        string? paperSizeName,
        int renderDpi,
        Action<string>? note,
        string? outputFile)
    {
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = queueName;

        if (!doc.PrinterSettings.IsValid)
            return $"The queue '{queueName}' is not installed on this PC.";

        doc.DocumentName = documentName;
        doc.PrinterSettings.Copies = (short)Math.Clamp(copies, 1, 99);

        if (!string.IsNullOrWhiteSpace(outputFile))
        {
            doc.PrinterSettings.PrintToFile = true;
            doc.PrinterSettings.PrintFileName = outputFile;
        }

        // OriginAtMargins false and zero margins: the caller composed a page, and
        // a driver silently insetting it would break the 1:1 mapping we are
        // going to all this trouble to preserve.
        doc.OriginAtMargins = false;
        doc.DefaultPageSettings.Landscape = false;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        var paperError = ApplyPaperSize(doc, image, renderDpi, paperSizeName, note, out var intended);
        if (paperError is not null) return paperError;

        doc.PrintPage += (_, e) =>
        {
            ReportPaperSubstitution(e, intended, note);
            DrawPage(e, image, renderDpi, note);
        };

        doc.Print();
        return null;
    }

    /// <summary>Asks for a page the exact size of the artwork.</summary>
    public const string AutoPaperSize = "auto";

    /// <summary>
    /// DEVMODE's <c>DMPAPER_USER</c>. PaperSize's constructor leaves RawKind at
    /// 0, which drivers read as "no opinion" and quietly replace with their
    /// default form; 256 is the value that actually means user-defined.
    /// </summary>
    private const int DmPaperUser = 256;

    /// <param name="intended">
    /// The page we asked for, in hundredths of an inch, or null when we left the
    /// driver alone. Recorded separately rather than read back from
    /// DefaultPageSettings, because a driver that rejects a size reverts that
    /// property — so reading it back would only ever tell us we got what we
    /// asked for, which is precisely the lie we want to catch.
    /// </param>
    private static string? ApplyPaperSize(
        PrintDocument doc, Bitmap image, int renderDpi, string? paperSizeName, Action<string>? note,
        out Size? intended)
    {
        intended = null;

        // Blank: leave the driver on whatever form it was set up with. This is
        // the conservative default on purpose — a receipt printer configured
        // for "72mm x Receipt" is already right, and overriding it is more
        // likely to break a working install than to fix one.
        if (string.IsNullOrWhiteSpace(paperSizeName)) return null;

        if (!string.Equals(paperSizeName, AutoPaperSize, StringComparison.OrdinalIgnoreCase))
        {
            var match = doc.PrinterSettings.PaperSizes
                .Cast<PaperSize>()
                .FirstOrDefault(p => string.Equals(p.PaperName, paperSizeName, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                var available = string.Join(", ", doc.PrinterSettings.PaperSizes.Cast<PaperSize>().Select(p => p.PaperName));
                return $"'{paperSizeName}' is not a paper size on '{doc.PrinterSettings.PrinterName}'. Available: {available}";
            }

            doc.DefaultPageSettings.PaperSize = match;
            intended = new Size(match.Width, match.Height);
            return null;
        }

        // "auto": a page the size of the artwork, which is what a variable-length
        // receipt wants. PaperSize is in hundredths of an inch.
        var widthHundredths = (int)Math.Round(image.Width / (float)renderDpi * 100f);
        var heightHundredths = (int)Math.Round(image.Height / (float)renderDpi * 100f);

        if (widthHundredths <= 0 || heightHundredths <= 0)
            return $"Image is {image.Width}x{image.Height} at {renderDpi} dpi, which is too small to place on a page.";

        try
        {
            doc.DefaultPageSettings.PaperSize =
                new PaperSize("MaplePrint", widthHundredths, heightHundredths) { RawKind = DmPaperUser };
            intended = new Size(widthHundredths, heightHundredths);
        }
        catch (Exception ex)
        {
            note?.Invoke($"Driver refused a custom page ({ex.Message}); using its default form.");
        }

        return null;
    }

    /// <summary>
    /// Drivers substitute a page size they don't support without complaining,
    /// which turns "my receipt printed on A4" into a mystery. If what we asked
    /// for isn't what we got, say so.
    /// </summary>
    private static void ReportPaperSubstitution(PrintPageEventArgs e, Size? intended, Action<string>? note)
    {
        if (intended is not { } want) return;

        var actual = e.PageSettings.PaperSize;
        if (actual is null) return;

        // A little slack: rounding to hundredths of an inch is not exact.
        if (Math.Abs(actual.Width - want.Width) <= 2 && Math.Abs(actual.Height - want.Height) <= 2) return;

        note?.Invoke(
            $"Driver ignored the requested {want.Width / 100f:0.00}x{want.Height / 100f:0.00}in page "
          + $"and used '{actual.PaperName}' ({actual.Width / 100f:0.00}x{actual.Height / 100f:0.00}in) instead.");
    }

    private static void DrawPage(PrintPageEventArgs e, Bitmap image, int renderDpi, Action<string>? note)
    {
        var g = e.Graphics;
        if (g is null) return;

        // Nearest-neighbour and no smoothing: a thermal head prints a dot or it
        // doesn't, so interpolating between them only produces grey mush. On a
        // higher-resolution device the upscale is a clean integer-ish multiple,
        // which is exactly where nearest-neighbour looks best.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;
        g.CompositingQuality = CompositingQuality.HighSpeed;

        // Inches, stated explicitly, with an explicit destination rectangle.
        //
        // Letting DrawImage(image, 0, 0) infer the size from the bitmap's own
        // resolution also works, but it hides the arithmetic behind two
        // implicit conversions — the page unit, and the image DPI — and the
        // scale-to-fit branch below needs the numbers anyway. Naming the unit
        // means the size on paper can be read straight off this code.
        g.PageUnit = GraphicsUnit.Inch;
        image.SetResolution(renderDpi, renderDpi);

        var widthInches = image.Width / (float)renderDpi;
        var heightInches = image.Height / (float)renderDpi;
        var printableWidthInches = e.PageSettings.PrintableArea.Width / 100f;

        if (printableWidthInches > 0.05f && widthInches > printableWidthInches + 0.02f)
        {
            // Wider than the paper. Scaling loses dot alignment, but silently
            // clipping the right-hand side of a receipt would be worse — and the
            // note says the caller rendered too wide.
            var scale = printableWidthInches / widthInches;

            note?.Invoke($"Artwork is {widthInches:0.00}in wide but the paper takes "
                       + $"{printableWidthInches:0.00}in; scaled to {scale:P0}. "
                       + $"Render at {printableWidthInches * renderDpi:0} dots to keep it sharp.");

            widthInches *= scale;
            heightInches *= scale;
        }

        g.DrawImage(image, new RectangleF(0, 0, widthInches, heightInches));

        e.HasMorePages = false;
    }
}
