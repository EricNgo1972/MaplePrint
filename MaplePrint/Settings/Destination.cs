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
    /// Which command set fires the cash drawer wired into this printer's DK
    /// port — <c>star</c> or <c>escpos</c>, per <see cref="DrawerCommands"/>.
    ///
    /// <para><b>Blank means there is no drawer</b>, which is every destination
    /// until somebody sets one up. Emptiness is the flag deliberately: a
    /// separate "drawer enabled" switch would create a second way to say the
    /// same thing, and a state where a drawer is configured but off is one more
    /// thing to get wrong at a counter. Same idea as a blank <see cref="Queue"/>
    /// meaning "not finished being set up".</para>
    ///
    /// <para>Declared, never sniffed. A Star and an Epson disagree about what a
    /// pulse byte means, so a wrong guess here is a till that opens
    /// intermittently — which reads as a faulty drawer, not as a setting.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DrawerCommand { get; set; }

    /// <summary>
    /// How long the solenoid is driven, in <b>milliseconds</b> — never in
    /// device units, because those differ per command set. 200 ms suits most
    /// drawers; some need 250–300 ms to release the latch.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DrawerPulseMs { get; set; }

    /// <summary>
    /// Which peripheral the printer drives: 1 (the usual single drawer) or 2 (a
    /// second drawer, if one is fitted). Configurable, but nothing in the app
    /// builds a workflow around a second drawer.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DrawerUnit { get; set; }

    /// <summary>The default pulse. Long enough for the common latch, short
    /// enough not to sound like a fault.</summary>
    public const int DefaultDrawerPulseMs = 200;

    /// <summary>True when a drawer is wired to this printer and set up.</summary>
    [JsonIgnore]
    public bool HasDrawer => !string.IsNullOrWhiteSpace(DrawerCommand);

    [JsonIgnore]
    public int EffectiveDrawerPulseMs => DrawerPulseMs is > 0 ? DrawerPulseMs.Value : DefaultDrawerPulseMs;

    [JsonIgnore]
    public int EffectiveDrawerUnit => DrawerUnit == 2 ? 2 : 1;

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

/// <summary>
/// The command sets that can fire a cash drawer, as stored in
/// <c>settings.json</c>. Strings rather than an enum, matching
/// <see cref="PrinterLanguages"/> and letting blank mean "no drawer".
/// </summary>
public static class DrawerCommands
{
    /// <summary>Star native — TSP100 / TSP143III.</summary>
    public const string Star = "star";

    /// <summary>Epson ESC/POS, and the many clones of it.</summary>
    public const string EscPos = "escpos";
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

/// <summary>One roll of paper, named the way the box it came in is.</summary>
public sealed record PaperWidth(string Name, int DotsAt203Dpi);

/// <summary>
/// The paper an operator can actually identify, so that setting a destination up
/// is answering "what is loaded?" rather than "how many dots across is the
/// printable area?".
///
/// <para>Dots remain the stored truth — they are what callers are told and what
/// decides the artwork's physical size (<c>pixels ÷ RenderDpi</c>). This is a
/// shortcut that fills them in, not a replacement: an odd printer can still have
/// its width typed straight in.</para>
///
/// <para>Held at 203 dpi, the resolution of nearly every thermal printer, and
/// scaled for the rest. Receipt figures are the industry ones (576 / 384) rather
/// than derived, because those are what the drivers and every other POS assume;
/// labels are simply their width times the resolution.</para>
/// </summary>
public static class PaperWidths
{
    /// <summary>Shown when the width matches no known roll. Selecting it changes
    /// nothing — it means "leave the dots alone".</summary>
    public const string Custom = "Custom…";

    public static readonly PaperWidth[] All =
    [
        new("80 mm receipt", DotWidths.Receipt80mm),
        new("58 mm receipt", 384),
        new("2″ label", 406),
        new("2.2″ label", DotWidths.Label2Inch),
        new("3″ label", 609),
        new("4″ label", 812),
    ];

    private static int Scale(int dotsAt203, int dpi) =>
        dpi is <= 0 or Destination.DefaultRenderDpi
            ? dotsAt203
            : (int)Math.Round(dotsAt203 * (dpi / (double)Destination.DefaultRenderDpi));

    /// <summary>Dots for a named roll at this resolution, or null for
    /// <see cref="Custom"/> and anything unrecognised.</summary>
    public static int? DotsFor(string? name, int dpi)
    {
        var paper = All.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return paper is null ? null : Scale(paper.DotsAt203Dpi, dpi);
    }

    /// <summary>The roll a width corresponds to, for showing an existing
    /// destination as the paper it is rather than as a number.</summary>
    public static string NameFor(int dots, int dpi)
    {
        if (dots <= 0) return Custom;

        foreach (var paper in All)
            if (Scale(paper.DotsAt203Dpi, dpi) == dots)
                return paper.Name;

        return Custom;
    }
}
