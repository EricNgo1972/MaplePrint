using System.Text;

using MaplePrint.Settings;

namespace MaplePrint.Printing;

/// <summary>One command an operator can send to a printer, named the way they
/// would ask for it rather than the way the manual spells it.</summary>
/// <param name="Title">What it does, in plain words.</param>
/// <param name="Detail">The command itself, shown beside the title so what left
/// the PC can be matched against the printer's manual.</param>
public sealed record PrinterCommand(string Title, string Detail, byte[] Bytes)
{
    /// <summary>
    /// What should be seen at the printer if it acted on this, and what to do
    /// next. Spooled is not obeyed — every command here is written one way, with
    /// no reply — so the only proof any of them worked is paper. Carried on the
    /// command because the check differs per command, and the caller sending it
    /// has no business knowing which.
    /// </summary>
    public required string Afterwards { get; init; }

    /// <summary>
    /// Ask before sending. Set only where the printer loses something a shop
    /// tuned and would have to put back by hand.
    /// </summary>
    public bool Confirm { get; init; }

    /// <summary>
    /// What is lost, for the confirmation to state. A prompt that only asks "are
    /// you sure?" moves the decision without informing it, and gets clicked
    /// through.
    /// </summary>
    public string? Consequence { get; init; }
}

/// <summary>
/// The system functions a printer will perform on command — calibrate, report,
/// restart, default — as a catalogue the UI can offer by name.
///
/// <para>Here because the alternative is what it replaced: a technician
/// hand-writing a curl at a command prompt, with the printer's language, the
/// station's address and the destination's name all to get right at once, from
/// memory, while a shop waits. The bytes are three characters; knowing which
/// three is the whole difficulty, and that belongs in the app rather than in
/// somebody's notes.</para>
///
/// <para>Everything is one-way. These printers answer status queries only over a
/// channel <see cref="RawPrinter"/> does not have, so nothing here reads a
/// reply, and every command reports itself through paper instead — which is what
/// <see cref="PrinterCommand.Afterwards"/> is for.</para>
///
/// <para>Restoring factory settings is included, and is <c>^JUF</c> — the
/// printer's own configuration. <c>^JUN</c>, which defaults the network stack,
/// is deliberately absent: it clears the IP address of the one machine that most
/// needs reaching remotely, and a command that can strand its own printer does
/// not belong behind a menu item. The four-flash Feed button reset is the same
/// hazard in hardware, which is why it stays a walk to the printer.</para>
/// </summary>
public static class PrinterCommands
{
    /// <summary>
    /// What can usefully be sent to <paramref name="destination"/>, chosen by
    /// its declared language rather than its name — a second ZPL printer called
    /// anything at all still gets the ZPL commands.
    ///
    /// <para>Empty for a <c>document</c> destination: it is driven through a
    /// Windows driver and has no command language to speak to.</para>
    /// </summary>
    public static IReadOnlyList<PrinterCommand> For(Destination destination)
    {
        if (destination.Kind != DestinationKind.Raw) return [];

        return string.Equals(destination.Language, PrinterLanguages.Zpl, StringComparison.OrdinalIgnoreCase)
            ? Zpl
            // Blank is the common case — an operator ticks "raw" for a receipt
            // printer and moves on. ESC/POS is what that printer speaks.
            : EscPos;
    }

    /// <summary>
    /// Ordered as they are offered: look before you touch. The reports come
    /// first, then the adjustment that fixes the common fault, then recovery,
    /// then the one that discards settings.
    ///
    /// <para>Computed rather than stored, because a static initialiser here
    /// would run before the commands below it are assigned and would capture a
    /// list of nulls. Built on each menu open, which is as often as it matters.</para>
    /// </summary>
    public static IReadOnlyList<PrinterCommand> Zpl =>
    [
        PrintConfiguration,
        CalibrateMedia,
        ExitDiagnostics,
        RestartPrinter,
        RestoreFactorySettings,
    ];

    public static IReadOnlyList<PrinterCommand> EscPos => [EscPosReset];

    /// <summary>
    /// Prints the printer's own settings — label length, media type, sensor,
    /// darkness, print width, firmware.
    ///
    /// <para>The first thing to send to a printer that is behaving differently
    /// from an identical one beside it, because it turns "same model, same job,
    /// different output" into two labels that can be held side by side. The
    /// difference is nearly always a number on this list.</para>
    /// </summary>
    public static PrinterCommand PrintConfiguration { get; } =
        new("Print configuration", "~WC", Ascii("~WC"))
        {
            Afterwards = "A configuration label should come out. Print one from a printer that is working "
                       + "too, and compare LABEL LENGTH, MEDIA TYPE and SENSOR SELECT first.",
        };

    /// <summary>
    /// Measures the media and sets the sensors to it.
    ///
    /// <para>The fix for a printer that feeds a blank label between every
    /// printed one: on gap media the feed distance comes from the printer's
    /// stored length, not from the job, so a wrong length there survives every
    /// correct label sent to it.</para>
    ///
    /// <para>Costs one to four labels, which is the cheapest diagnostic in this
    /// menu and worth spending before anything else is suspected.</para>
    /// </summary>
    public static PrinterCommand CalibrateMedia { get; } =
        new("Calibrate media", "^XA~JC^XZ", Ascii("^XA~JC^XZ"))
        {
            Afterwards = "The printer feeds one to four labels while it measures. Then press its Feed "
                       + "button: exactly one blank label should come out. Two means it is still wrong.",
        };

    /// <summary>
    /// Leaves Communications Diagnostics mode, where a Zebra prints every byte
    /// it receives as a character with its hex value under it instead of
    /// interpreting it — so a perfectly good label comes out as pages of
    /// <c>^FO16,16^A0N,24,24</c> in two rows.
    ///
    /// <para>The one command a printer in that mode still acts on, which is why
    /// it is reachable at all: everything else is echoed rather than obeyed. It
    /// undoes a <c>~JD</c>; the same mode entered from the Feed button at
    /// power-on needs the power removed, and no command can substitute.</para>
    /// </summary>
    public static PrinterCommand ExitDiagnostics { get; } =
        new("Exit diagnostics", "~JE", Ascii("~JE"))
        {
            Afterwards = "Print a test label. If it still comes out as rows of hex, the printer was put "
                       + "into diagnostics mode from its Feed button, and only removing its power clears that.",
        };

    /// <summary>
    /// A power-on reset — the remote equivalent of switching the printer off and
    /// on, for the counter PC's printer that nobody is standing next to.
    ///
    /// <para>Anything already spooled is lost, which on a printer mid-job is the
    /// point rather than a side effect.</para>
    /// </summary>
    public static PrinterCommand RestartPrinter { get; } =
        new("Restart printer", "~JR", Ascii("~JR"))
        {
            Afterwards = "The printer restarts, which takes a few seconds. Anything it had not finished "
                       + "printing is gone; send those jobs again.",
        };

    /// <summary>
    /// Reloads the factory configuration and saves it, so it survives the next
    /// power cycle — <c>^JUF</c> without <c>^JUS</c> is undone by switching the
    /// printer off, which is not what anybody defaulting a printer means.
    ///
    /// <para>The blunt instrument, and last on purpose: darkness and print width
    /// go back to factory values along with everything else, so a shop that had
    /// its labels tuned loses that tuning. Print the configuration first.</para>
    ///
    /// <para>Printer settings only. The network stack is left alone — see the
    /// note on this class.</para>
    /// </summary>
    public static PrinterCommand RestoreFactorySettings { get; } =
        new("Restore factory settings", "^XA^JUF^JUS^XZ", Ascii("^XA^JUF^JUS^XZ"))
        {
            Confirm = true,
            Consequence = "Darkness, print width, label length and every other printer setting go back to "
                        + "factory values, and are saved. Anything tuned for this shop's labels is lost. "
                        + "Print the configuration first if you have not, so the settings can be put back.",
            Afterwards = "The printer defaults, then calibrates itself. Set darkness and print width to "
                       + "match a printer that is working before trusting its output.",
        };

    /// <summary>
    /// <c>ESC @</c> — initialise. Clears whatever mode a half-finished job left
    /// behind: double-width text, an unterminated bitmap, a character set the
    /// last receipt switched to and never switched back.
    /// </summary>
    public static PrinterCommand EscPosReset { get; } =
        new("Reset printer", "ESC @", [0x1B, 0x40])
        {
            Afterwards = "Print a test slip. Text should be back to its normal size and character set.",
        };

    private static byte[] Ascii(string command) => Encoding.ASCII.GetBytes(command);
}
