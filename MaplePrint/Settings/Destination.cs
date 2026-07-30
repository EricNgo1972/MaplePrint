using System.Text.Json.Serialization;

namespace MaplePrint.Settings;

/// <summary>
/// How a destination is driven. This is *declared* at setup, never sniffed:
/// almost every Windows queue claims to accept RAW, so detection would be
/// worthless, and paper-size heuristics fail on exactly the odd hardware you
/// meet in the field. When detection is wrong it is wrong silently, and the
/// shop pays in wasted paper. The person installing the printer knows.
/// </summary>
public enum DestinationKind
{
    /// <summary>
    /// The printer only understands what its Windows driver produces. Callers
    /// send a rendered page and MaplePrint draws it through the driver. This is
    /// every laser and inkjet, and also raster receipt printers like the Star
    /// TSP100 family, which have no command language at all.
    /// </summary>
    Document,

    /// <summary>
    /// The printer speaks a byte language — ZPL, ESC/POS, PCL. Callers send
    /// device bytes and MaplePrint spools them untouched.
    /// </summary>
    Raw
}

/// <summary>
/// A name that callers address, mapped on this PC to a Windows queue.
///
/// Destinations replace the old fixed <c>receipt</c>/<c>label</c> roles: adding
/// a third printer becomes a settings entry rather than a code change, which is
/// the real test of whether this server is generic.
/// </summary>
public sealed class Destination
{
    /// <summary>What callers put in <c>destination</c>. Matched case-insensitively.</summary>
    public string Name { get; set; } = "";

    /// <summary>The Windows print queue. Blank means this destination isn't finished being set up.</summary>
    public string Queue { get; set; } = "";

    public DestinationKind Kind { get; set; } = DestinationKind.Document;

    /// <summary>
    /// For <see cref="DestinationKind.Raw"/>: which language the caller should
    /// send. Informational — MaplePrint never interprets the payload — but it
    /// makes rejection messages specific instead of merely negative.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    /// <summary>
    /// Printable width in printer dots, advertised to callers so they render at
    /// the right size instead of hard-coding it at their end. 0 when unknown.
    /// </summary>
    public int DotWidth { get; set; }

    /// <summary>
    /// The resolution the caller's artwork is drawn at, which is what decides
    /// its physical size: <c>pixels ÷ RenderDpi = inches</c>.
    ///
    /// Declared rather than taken from the device, because those are different
    /// numbers. Mapping one bitmap pixel to one *device* dot only gives the
    /// right size when the two happen to agree — send a 576-dot receipt to a
    /// 600 dpi laser that way and it prints 0.96in wide, a stamp in the corner
    /// of a sheet of A4. With this declared at 203, the same bitmap is 2.84in
    /// on any printer: exactly 1:1 on the 203 dpi thermal head it was drawn
    /// for, and correctly scaled everywhere else.
    /// </summary>
    public int RenderDpi { get; set; } = DefaultRenderDpi;

    /// <summary>The thermal standard, and what both the Star and the Zebra run at.</summary>
    public const int DefaultRenderDpi = 203;

    /// <summary>
    /// Which page the driver should use, for <see cref="DestinationKind.Document"/>:
    /// <list type="bullet">
    /// <item>blank — leave the driver on its configured default. Correct for a
    /// receipt printer whose form is already <c>72mm x Receipt</c>, and the safe
    /// choice generally, since it respects what the installer set up.</item>
    /// <item>a form name, e.g. <c>72mm x Receipt</c> or <c>A4</c> — select it.</item>
    /// <item><c>auto</c> — build a page the exact size of the bitmap. Ideal in
    /// theory, but many drivers silently ignore custom sizes, so it is opt-in
    /// rather than the default.</item>
    /// </list>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaperSizeName { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// JsonIgnore because System.Text.Json serializes get-only properties, and
    /// a computed value written into the settings file reads as a setting.
    /// </summary>
    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Queue);
}

/// <summary>The two destinations seeded on a fresh install.</summary>
public static class DestinationNames
{
    public const string Receipt = "receipt";
    public const string Label = "label";
}

public static class PrinterLanguages
{
    public const string EscPos = "escpos";
    public const string Zpl = "zpl";
}

public static class DotWidths
{
    /// <summary>80 mm roll, 72 mm printable, at 203 dpi.</summary>
    public const int Receipt80mm = 576;

    /// <summary>2.20″ printable at 203 dpi — Zebra LP 2824 and friends.</summary>
    public const int Label2Inch = 448;
}
