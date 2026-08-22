using System.Runtime.Versioning;

namespace MaplePrint.Printing;

/// <summary>
/// Where bytes go. A seam, because the drawer sequences and the three drawer
/// scenarios are identical whatever the printer is plugged into — only the sink
/// differs.
///
/// <para>Returns null on success and a human-readable reason otherwise, rather
/// than throwing: that is the convention <see cref="RawPrinter"/> and
/// <see cref="DocumentPrinter"/> already follow, and a jammed printer must not
/// take down the request that produced the receipt.</para>
/// </summary>
public interface IPrinterTransport
{
    Task<string?> SendAsync(ReadOnlyMemory<byte> bytes, string documentName, CancellationToken ct = default);
}

/// <summary>
/// How a printer is addressed. <see cref="DeviceKey"/> is its identity for
/// serialization — two jobs that share one are two jobs for one piece of
/// hardware, whatever destination, station or caller they arrived from.
/// </summary>
public abstract record PrinterEndpoint
{
    /// <summary>Transport identity. Not the destination name, not the station.</summary>
    public abstract string DeviceKey { get; }

    /// <summary>How every printer in the field is reached today: a Windows print
    /// queue, RAW datatype, driver renderer bypassed.</summary>
    public sealed record WindowsQueue(string QueueName) : PrinterEndpoint
    {
        public override string DeviceKey => "queue:" + (QueueName ?? "").Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Raw TCP to a printer's own port 9100.
    ///
    /// <para>Declared but not implemented. Nothing in the field is addressed this
    /// way — a TSP143IIILAN reaches this PC as a Windows queue over a Standard
    /// TCP/IP port — so building it now would add a second way to name a printer
    /// that no destination can express. The trigger to build it is named in the
    /// README: if hardware verification shows futurePRNT intercepting RAW jobs,
    /// this is the way around it for LAN units.</para>
    /// </summary>
    public sealed record Network(string Host, int Port = 9100) : PrinterEndpoint
    {
        public override string DeviceKey => $"net:{(Host ?? "").Trim().ToLowerInvariant()}:{Port}";
    }

    /// <summary>
    /// A device path such as <c>/dev/usb/lp0</c>.
    ///
    /// <para>Declared but not implemented, and cannot be: MaplePrint targets
    /// <c>net8.0-windows</c> and is a WinForms process, so there is no host for
    /// it to run on. Present so the seam is shaped for it rather than reshaped
    /// later.</para>
    /// </summary>
    public sealed record UsbDevice(string Path) : PrinterEndpoint
    {
        public override string DeviceKey => "usb:" + (Path ?? "").Trim();
    }
}

/// <summary>
/// Sends bytes to a Windows print queue with the RAW datatype, which is what
/// puts them in front of the printer's own command parser instead of the
/// driver's renderer. The only transport that exists today.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsQueueTransport : IPrinterTransport
{
    private readonly string _queueName;

    public WindowsQueueTransport(string queueName) => _queueName = queueName;

    public Task<string?> SendAsync(ReadOnlyMemory<byte> bytes, string documentName, CancellationToken ct = default)
    {
        // RawPrinter blocks on the spooler. Off the calling thread, so a wedged
        // driver cannot starve the consumer draining this device's queue.
        return Task.Run(() => RawPrinter.Send(_queueName, bytes.ToArray(), documentName), ct);
    }
}

/// <summary>Resolves an endpoint to the transport that can serve it.</summary>
[SupportedOSPlatform("windows")]
public static class PrinterTransports
{
    /// <summary>
    /// The transport for this endpoint, or null with a reason for one that is
    /// declared but not built. Never guesses — an endpoint we cannot serve is a
    /// refusal, not a fallback to some other printer.
    /// </summary>
    public static (IPrinterTransport? Transport, string? Problem) For(PrinterEndpoint endpoint) => endpoint switch
    {
        PrinterEndpoint.WindowsQueue q => (new WindowsQueueTransport(q.QueueName), null),

        PrinterEndpoint.Network n =>
            (null, $"This build cannot send to {n.Host}:{n.Port} directly — printers are reached as Windows queues."),

        PrinterEndpoint.UsbDevice u =>
            (null, $"This build cannot write to '{u.Path}' — that is a Linux device path and MaplePrint is a Windows app."),

        _ => (null, "Unknown printer endpoint.")
    };
}
