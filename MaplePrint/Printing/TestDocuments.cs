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
}
