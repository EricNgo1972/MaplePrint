using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.Versioning;
using System.Text;

namespace MaplePrint.Printing;

/// <summary>
/// Self-contained test payloads, so a technician can prove the paper path works
/// before the server is even reachable. These are the only two places in the
/// agent that know what a printer language looks like — real jobs arrive
/// pre-encoded from MapleCoffee.
/// </summary>
public static class TestDocuments
{
    /// <summary>
    /// ESC/POS test slip for an 80mm thermal printer (72mm print width, 42
    /// columns at font A) — matches the Star TSP143's driver form.
    /// </summary>
    public static byte[] Receipt(string stationName)
    {
        var esc = new List<byte>();

        void Raw(params byte[] b) => esc.AddRange(b);
        void Text(string s) => esc.AddRange(Encoding.ASCII.GetBytes(s));

        Raw(0x1B, 0x40);              // ESC @   initialise
        Raw(0x1B, 0x61, 0x01);        // ESC a 1 centre
        Raw(0x1B, 0x45, 0x01);        // ESC E 1 bold on
        Text("MaplePrint\n");
        Raw(0x1B, 0x45, 0x00);        // bold off
        Text("Receipt printer test\n\n");
        Raw(0x1B, 0x61, 0x00);        // ESC a 0 left
        Text($"Station : {stationName}\n");
        Text($"Printed : {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        Text(new string('-', 42) + "\n");
        Text("If you can read this line in full, the\n");
        Text("paper width and column count are right.\n");
        Text(new string('-', 42) + "\n");
        Text("1234567890" + "1234567890" + "1234567890" + "123456789012\n");
        Raw(0x0A, 0x0A, 0x0A);        // feed clear of the cutter

        // The Star's driver is set to Partial Cut per document, so we do NOT
        // emit a cut here — two cutters firing on one slip wastes a receipt's
        // worth of paper every time.

        return esc.ToArray();
    }

    /// <summary>
    /// ZPL II test label for the Zebra LP 2824 Plus: 203 dpi, 2.20" print width
    /// (448 dots). <c>^PW</c> is the print width and <c>^LL</c> the label length,
    /// both in dots.
    /// </summary>
    public static byte[] Label(string stationName)
    {
        var zpl = $"""
            ^XA
            ^PW448
            ^LL203
            ^CF0,28
            ^FO16,12^FDMaplePrint^FS
            ^CF0,20
            ^FO16,50^FDLabel printer test^FS
            ^FO16,78^FD{Sanitize(stationName)}^FS
            ^FO16,104^FD{DateTime.Now:yyyy-MM-dd HH:mm}^FS
            ^FO16,134^GB416,2,2^FS
            ^FO16,146^FDEdge-to-edge check: |{new string('-', 28)}|^FS
            ^XZ

            """;

        return Encoding.ASCII.GetBytes(zpl);
    }

    /// <summary>
    /// ZPL delimits fields with <c>^</c> and <c>~</c>; a station name containing
    /// either would truncate the label or start a command. Strip rather than
    /// escape — this is a diagnostic slip, not user content.
    /// </summary>
    private static string Sanitize(string value) =>
        new(value.Where(c => c is not ('^' or '~') && !char.IsControl(c)).ToArray());

    /// <summary>
    /// A calibration slip for a <c>document</c> destination — the path a raster
    /// receipt printer like the Star TSP143 actually uses.
    ///
    /// Deliberately measurable rather than decorative. The rules run the full
    /// bitmap width, so if they don't reach both paper edges the render width is
    /// wrong; and the scale bar is exactly one inch of dots, so a ruler against
    /// the paper settles whether one bitmap pixel became one printer dot. Those
    /// two checks are the only way to know the drawing maths is right without
    /// reading the driver's mind.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Bitmap CalibrationImage(string stationName, int dotWidth = DefaultReceiptDots, int dpi = DefaultDpi)
    {
        if (dotWidth <= 0) dotWidth = DefaultReceiptDots;
        if (dpi <= 0) dpi = DefaultDpi;

        const int margin = 12;

        // Sized to the content: this prints on a roll, and every unused dot of
        // height is paper the shop throws away on each diagnostic.
        const int height = 300;

        var bmp = new Bitmap(dotWidth, height);
        bmp.SetResolution(dpi, dpi);

        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var black = new SolidBrush(Color.Black);
        using var pen = new Pen(Color.Black, 2);
        using var title = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 19, FontStyle.Regular, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);

        // Edge-to-edge rules: these must touch both sides of the paper.
        g.FillRectangle(black, 0, 0, dotWidth, 3);
        g.FillRectangle(black, 0, height - 3, dotWidth, 3);

        var y = 18f;
        g.DrawString("MaplePrint", title, black, margin, y);
        y += 40;
        g.DrawString("Print calibration", body, black, margin, y);
        y += 28;
        g.DrawString(stationName, small, black, margin, y);
        y += 24;
        g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), small, black, margin, y);
        y += 32;

        g.FillRectangle(black, margin, y, dotWidth - margin * 2, 1);
        y += 18;

        // One inch of dots, with end ticks to measure between.
        var barLeft = (float)margin;
        var barRight = margin + dpi;
        g.DrawLine(pen, barLeft, y + 12, barRight, y + 12);
        g.DrawLine(pen, barLeft, y + 4, barLeft, y + 20);
        g.DrawLine(pen, barRight, y + 4, barRight, y + 20);
        g.DrawString($"= 1 inch ({dpi} dots)", small, black, barRight + 10, y + 2);
        y += 34;

        g.DrawString($"Rendered {dotWidth} dots wide at {dpi} dpi.", small, black, margin, y);
        y += 22;
        g.DrawString("Rules above and below should touch both edges.", small, black, margin, y);
        y += 24;

        // Fine-detail patches. Anything that resamples the bitmap — a scale, a
        // driver dither, a caller rendering at the wrong width — turns the
        // 1-dot checker into uneven grey mush instead of a sharp pattern. This
        // is the check that predicts whether a QR code will scan, because a QR
        // is exactly this: small hard-edged squares that must survive intact.
        Checker(g, black, margin, (int)y, 48, 26, 1);
        Checker(g, black, margin + 60, (int)y, 48, 26, 4);
        g.DrawString("1-dot / 4-dot: must stay sharp", small, black, margin + 118, y + 4);

        return bmp;
    }

    /// <summary>
    /// A checkerboard of <paramref name="cell"/>-dot squares. Bitmap coordinates
    /// are dots here, and smoothing is off, so each square lands on exact dot
    /// boundaries — which is the point: any softness on paper came from the
    /// print path, not from this.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void Checker(Graphics g, Brush brush, int left, int top, int width, int height, int cell)
    {
        for (var x = 0; x < width; x += cell)
        {
            for (var y = 0; y < height; y += cell)
            {
                if ((x / cell + y / cell) % 2 != 0) continue;

                var w = Math.Min(cell, width - x);
                var h = Math.Min(cell, height - y);
                g.FillRectangle(brush, left + x, top + y, w, h);
            }
        }
    }

    /// <summary>80 mm roll, 72 mm printable, at 203 dpi.</summary>
    public const int DefaultReceiptDots = 576;

    /// <summary>The thermal standard, and what both the Star and the Zebra run at.</summary>
    public const int DefaultDpi = 203;
}
