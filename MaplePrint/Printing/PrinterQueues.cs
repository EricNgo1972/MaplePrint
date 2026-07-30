using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace MaplePrint.Printing;

/// <summary>
/// The Windows print queues installed on this PC. Reported to the server on
/// connect so an administrator can pick queues from a list instead of typing
/// <c>Star TSP100 Cutter (TSP143)</c> by hand and getting it subtly wrong.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrinterQueues
{
    public static IReadOnlyList<string> Installed()
    {
        try
        {
            return PrinterSettings.InstalledPrinters.Cast<string>().ToArray();
        }
        catch
        {
            // A broken spooler shouldn't stop the agent connecting; it just
            // means the admin types the queue name instead of picking it.
            return [];
        }
    }

    /// <summary>True if a queue by this exact name is installed. Name matching is
    /// what the spooler itself does, so it is case-insensitive but not fuzzy.</summary>
    public static bool Exists(string printerName) =>
        !string.IsNullOrWhiteSpace(printerName)
        && Installed().Any(p => string.Equals(p, printerName, StringComparison.OrdinalIgnoreCase));
}
