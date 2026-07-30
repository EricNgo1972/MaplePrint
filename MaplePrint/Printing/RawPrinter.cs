using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MaplePrint.Printing;

/// <summary>
/// Hands raw device bytes to a Windows print queue.
///
/// RAW means the bytes reach the device untouched — the driver does no
/// rendering, no page composition, no scaling. That is precisely what both of
/// our printers want, and it is why one method serves both: an ESC/POS receipt
/// and a ZPL label are the same problem at this layer, differing only in the
/// bytes the server chose to send. Nothing here parses or validates the
/// payload, which is deliberate — the agent stays dumb so that changing a
/// receipt's layout is a server deploy, not a visit to the shop.
///
/// Lifted from MapleKiosk's <c>SystemPrinterReceiptPrinter.WindowsRawPrint</c>,
/// where this same P/Invoke sequence is already in production. There is no
/// managed API for device-bytes printing on .NET Core, so P/Invoke it is.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDataType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string src, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFO di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDefaultPrinter(System.Text.StringBuilder? buffer, ref int size);

    /// <summary>
    /// Sends <paramref name="bytes"/> to <paramref name="printerName"/> as a RAW
    /// job. Returns null on success, otherwise a human-readable reason — the
    /// caller reports it back to the server rather than throwing, because a
    /// jammed printer must not take down the connection.
    /// </summary>
    public static string? Send(string printerName, byte[] bytes, string documentName = "MaplePrint job")
    {
        if (bytes.Length == 0) return "Nothing to print (empty payload).";

        if (string.IsNullOrWhiteSpace(printerName))
        {
            var resolved = DefaultPrinter();
            if (resolved is null) return "No printer was named and this machine has no default printer.";
            printerName = resolved;
        }

        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero) || printer == IntPtr.Zero)
            return $"Could not open printer '{printerName}' (error {Marshal.GetLastWin32Error()}). Is the queue still installed?";

        var unmanaged = IntPtr.Zero;
        var docStarted = false;
        var pageStarted = false;
        try
        {
            var doc = new DOCINFO
            {
                pDocName = documentName,
                pOutputFile = null,
                // RAW: pass the device bytes through untouched.
                pDataType = "RAW"
            };

            if (!StartDocPrinter(printer, 1, ref doc))
                return $"Printer '{printerName}' rejected the job (error {Marshal.GetLastWin32Error()}).";
            docStarted = true;

            if (!StartPagePrinter(printer))
                return $"Printer '{printerName}' rejected the page (error {Marshal.GetLastWin32Error()}).";
            pageStarted = true;

            unmanaged = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, unmanaged, bytes.Length);

            if (!WritePrinter(printer, unmanaged, bytes.Length, out var written) || written != bytes.Length)
                return $"Only {written} of {bytes.Length} bytes reached '{printerName}'.";

            return null;
        }
        catch (Exception ex)
        {
            return $"Printer '{printerName}': {ex.Message}";
        }
        finally
        {
            // Unwind in the order the spooler expects, and only what we opened —
            // calling EndPage on a doc that never started is its own error.
            if (pageStarted) { try { EndPagePrinter(printer); } catch { } }
            if (docStarted) { try { EndDocPrinter(printer); } catch { } }
            if (unmanaged != IntPtr.Zero) Marshal.FreeCoTaskMem(unmanaged);
            ClosePrinter(printer);
        }
    }

    private static string? DefaultPrinter()
    {
        var size = 0;
        GetDefaultPrinter(null, ref size);
        if (size <= 0) return null;

        var buffer = new System.Text.StringBuilder(size);
        return GetDefaultPrinter(buffer, ref size) ? buffer.ToString() : null;
    }
}
