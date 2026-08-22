using MaplePrint.Server;
using MaplePrint.Settings;

namespace MaplePrint.Printing;

/// <summary>How one planned send reaches the printer.</summary>
public enum SendKind
{
    /// <summary>Device bytes, RAW datatype, driver renderer bypassed.</summary>
    Raw,

    /// <summary>A rendered page drawn through the Windows driver.</summary>
    Image
}

/// <summary>One buffer on its way out, in order.</summary>
public sealed record PlannedSend(SendKind Kind, byte[] Bytes, bool ContainsKick);

/// <summary>
/// Works out what actually goes down the wire for a job, and in what order.
///
/// <para>Pure, and separate from the spooling, because this is the part worth
/// asserting byte-for-byte: the three scenarios, and the one asymmetry between
/// them.</para>
///
/// <para><b>The asymmetry.</b> A <c>raw</c> destination takes the kick prepended
/// to the receipt — one job, drawer fires as the paper starts moving, which is
/// what the Star and Epson manuals both describe. A <c>document</c> destination
/// cannot: its receipt is a rendered page drawn through the driver's raster
/// renderer, and there is no byte stream to prepend to. The field TSP143 is
/// exactly that case, since the futurePRNT models are raster-only. So the kick
/// goes as its own RAW job immediately ahead of the page.</para>
///
/// <para>That is two spooler jobs rather than one connection. The rule it bends
/// — never open a second connection to kick the drawer — exists because the Star
/// LAN interface holds a single TCP session and a second one blocks. Nothing
/// here opens a socket: the spooler holds the device, preserves order per queue,
/// and both jobs go out inside one hold of this printer's dispatch queue.</para>
/// </summary>
public static class DrawerDispatch
{
    /// <summary>
    /// The sends for this job, in order. Empty payload with a kick is a no-sale;
    /// no kick is today's behaviour, untouched.
    /// </summary>
    public static IReadOnlyList<PlannedSend> Plan(
        Destination destination,
        IPrinterCommandSet? commandSet,
        string contentType,
        byte[] payload,
        bool openDrawer)
    {
        var isImage = contentType == ContentTypes.Image;

        if (!openDrawer || commandSet is null)
        {
            return payload.Length == 0
                ? []
                : [new PlannedSend(isImage ? SendKind.Image : SendKind.Raw, payload, ContainsKick: false)];
        }

        var kick = commandSet.KickDrawer(
            destination.EffectiveDrawerPulseMs,
            destination.EffectiveDrawerUnit).ToArray();

        // Scenario 3 — no sale. The kick alone; no paper moves.
        if (payload.Length == 0)
            return [new PlannedSend(SendKind.Raw, kick, ContainsKick: true)];

        // Scenario 1 on a raw destination — one job, kick first.
        if (!isImage)
        {
            var merged = new byte[kick.Length + payload.Length];
            kick.CopyTo(merged, 0);
            payload.CopyTo(merged, kick.Length);
            return [new PlannedSend(SendKind.Raw, merged, ContainsKick: true)];
        }

        // Scenario 1 on a document destination — kick, then the rendered page.
        return
        [
            new PlannedSend(SendKind.Raw, kick, ContainsKick: true),
            new PlannedSend(SendKind.Image, payload, ContainsKick: false)
        ];
    }

    /// <summary>
    /// Why this destination cannot open a drawer, or null if it can. Checked when
    /// a job asks for one, and again when settings are saved, so a printer that
    /// was set up wrong says so before a sale rather than during one.
    /// </summary>
    public static string? Problem(Destination destination)
    {
        if (!destination.HasDrawer)
            return $"Destination '{destination.Name}' has no cash drawer set up.";

        var set = PrinterCommandSets.For(destination.DrawerCommand);
        if (set is null)
        {
            var known = string.Join(", ", PrinterCommandSets.All.Select(s => $"'{s.Name}'"));
            return $"Destination '{destination.Name}' has drawer command '{destination.DrawerCommand}', "
                 + $"which is not one of {known}.";
        }

        var pulse = destination.EffectiveDrawerPulseMs;
        if (pulse < set.MinPulseMs || pulse > set.MaxPulseMs)
            return $"Destination '{destination.Name}' has a drawer pulse of {pulse}ms, but {set.Title} "
                 + $"can only express {set.MinPulseMs}–{set.MaxPulseMs}ms. Pick a value in that range — "
                 + "clamping it quietly would give the drawer a pulse the settings do not say.";

        if (destination.DrawerUnit is not (null or 1 or 2))
            return $"Destination '{destination.Name}' has drawer unit {destination.DrawerUnit}; "
                 + "a printer drives peripheral unit 1 or 2.";

        return null;
    }
}
