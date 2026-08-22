using MaplePrint.Settings;

namespace MaplePrint.Printing;

/// <summary>
/// The bytes that fire a cash drawer wired into a receipt printer's DK port.
///
/// <para>A seam rather than a constant, because the kick command is not standard
/// across brands. Epson's ESC/POS sets timing and fires in one command; Star's
/// native set separates them. Worse, <b>the time unit differs</b> — 2 ms on
/// ESC/POS, 10 ms on Star — so the same byte <c>0x14</c> means 200 ms on a Star
/// and 40 ms on an Epson. A 40 ms pulse does not throw most drawer latches, and
/// it fails intermittently: the solenoid clicks, the latch holds, and the shop
/// reports "the drawer randomly doesn't open".</para>
///
/// <para>That is why configuration stores <b>milliseconds, never bytes</b>, and
/// why the conversion lives here where the brand is known.</para>
/// </summary>
public interface IPrinterCommandSet
{
    /// <summary>The value stored in <c>settings.json</c> — see <see cref="DrawerCommands"/>.</summary>
    string Name { get; }

    /// <summary>How an operator would name this set.</summary>
    string Title { get; }

    /// <summary>The shortest pulse this set can express, which is its time unit.</summary>
    int MinPulseMs { get; }

    /// <summary>
    /// The longest pulse this set can express. Star's pulse byte is bounded at
    /// 127 units of 10 ms, ESC/POS's at 255 units of 2 ms — so the ceilings are
    /// nowhere near each other, and a pulse legal for one is not for the other.
    /// </summary>
    int MaxPulseMs { get; }

    /// <summary>
    /// The full kick sequence for <paramref name="unit"/> (1 or 2) at
    /// <paramref name="pulseMs"/>. Always complete — never a bare drive byte,
    /// since the pulse-width command is sticky on some firmware and a bare kick
    /// would inherit whatever width the previous job left behind.
    /// </summary>
    ReadOnlyMemory<byte> KickDrawer(int pulseMs, int unit = 1);
}

/// <summary>
/// Star native (raster / Star Line), which is what the TSP100 and TSP143III
/// speak. Timing and drive are separate commands:
/// <code>
/// ESC BEL n1 n2  ->  0x1B 0x07 n1 n2   set pulse, unit = 10 ms
/// BEL            ->  0x07              drive peripheral unit 1
/// SUB            ->  0x1A              drive peripheral unit 2
/// </code>
/// <para>The TSP100 series is raster-only and will not accept ESC/POS at all.
/// Some other Star models expose an ESC/POS emulation switch; that is not relied
/// on here — a Star is treated as a Star.</para>
/// </summary>
public sealed class StarCommandSet : IPrinterCommandSet
{
    public string Name => DrawerCommands.Star;
    public string Title => "Star (TSP100 / TSP143III)";

    public int MinPulseMs => 10;

    /// <summary>127 units of 10 ms — the pulse byte's ceiling.</summary>
    public int MaxPulseMs => 1270;

    public ReadOnlyMemory<byte> KickDrawer(int pulseMs, int unit = 1)
    {
        // Clamped, not cast. A bare (byte) cast of 1300 ms wraps to 2 ms, which
        // is a drawer that never opens and a number nobody would think to check.
        var n = (byte)Math.Clamp(pulseMs / 10, 1, 127);
        return new byte[] { 0x1B, 0x07, n, n, unit == 2 ? (byte)0x1A : (byte)0x07 };
    }
}

/// <summary>
/// Epson's ESC/POS, cloned by most of the market — Bixolon, Citizen, SNBC,
/// Xprinter, Rongta and the generic 80 mm units. One command sets timing and
/// fires:
/// <code>
/// ESC p m t1 t2  ->  0x1B 0x70 m t1 t2
///   m  = 0x00 -> pin 2 (drawer 1),  0x01 -> pin 5 (drawer 2)
///   t1 = on time, t2 = off time,    unit = 2 ms
/// </code>
/// </summary>
public sealed class EscPosCommandSet : IPrinterCommandSet
{
    public string Name => DrawerCommands.EscPos;
    public string Title => "ESC/POS (Epson, Bixolon, Citizen, …)";

    public int MinPulseMs => 2;

    /// <summary>255 units of 2 ms — the pulse byte's ceiling.</summary>
    public int MaxPulseMs => 510;

    public ReadOnlyMemory<byte> KickDrawer(int pulseMs, int unit = 1)
    {
        var t = (byte)Math.Clamp(pulseMs / 2, 1, 255);
        return new byte[] { 0x1B, 0x70, unit == 2 ? (byte)0x01 : (byte)0x00, t, t };
    }
}

/// <summary>
/// The command sets that exist, and how a destination's stored name resolves to
/// one.
/// </summary>
public static class PrinterCommandSets
{
    public static readonly IPrinterCommandSet Star = new StarCommandSet();
    public static readonly IPrinterCommandSet EscPos = new EscPosCommandSet();

    public static IReadOnlyList<IPrinterCommandSet> All => [Star, EscPos];

    /// <summary>
    /// The set a stored name means, or null for blank (no drawer) and for
    /// anything unrecognised — which the caller turns into a loud refusal rather
    /// than a guess, since guessing wrong here is a till that will not open.
    /// </summary>
    public static IPrinterCommandSet? For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var wanted = name.Trim();
        return All.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The set for a destination, or null when it has no drawer.</summary>
    public static IPrinterCommandSet? For(Destination destination) =>
        destination.HasDrawer ? For(destination.DrawerCommand) : null;

    /// <summary>
    /// A starting guess from the queue name. ESC/POS is the fallback because it
    /// is most of the market; Star is the named exception.
    ///
    /// <para>Never applied on its own. The Settings grid names both sets by the
    /// brands on the box, which is a better question to ask an operator than any
    /// guess — this exists so <c>--self-test</c> can assert the mapping, and so a
    /// caller that wants a default has one.</para>
    /// </summary>
    public static string GuessFor(string? queueName)
    {
        var name = queueName ?? "";

        var looksStar =
            name.Contains("star", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("tsp", StringComparison.OrdinalIgnoreCase);

        return looksStar ? DrawerCommands.Star : DrawerCommands.EscPos;
    }
}
