namespace MaplePrint.Server;

/// <summary>
/// What the caller is sending. Validated against the destination's declared
/// kind before anything reaches paper — the rule that turns "three ejected
/// pages and a puzzled afternoon" into one legible refusal.
/// </summary>
public static class ContentTypes
{
    /// <summary>A rendered page. The universal path: it drives any printer with a Windows driver.</summary>
    public const string Image = "image";

    /// <summary>Device bytes in the printer's own language — ZPL, ESC/POS, PCL.</summary>
    public const string Raw = "raw";
}

/// <summary>
/// A job. <see cref="PayloadBase64"/> is either a rendered image or device
/// bytes, per <see cref="ContentType"/>. MaplePrint never inspects, reformats
/// or validates the content itself; it is a paper path, not a renderer.
/// </summary>
/// <param name="Destination">A configured destination name, e.g. <c>receipt</c>.</param>
/// <param name="ContentType">
/// <see cref="ContentTypes"/>. Optional — when omitted, the destination's kind
/// implies it, which is right in every case except a caller that has got them
/// confused, and that caller wants the error.
/// </param>
/// <param name="Target">Deprecated alias for <see cref="Destination"/>.</param>
public sealed record PrintRequest(
    string? Destination = null,
    string? ContentType = null,
    string? PayloadBase64 = null,
    string? Description = null,
    string? JobId = null,
    int Copies = 1,
    string? Target = null)
{
    /// <summary>The destination the caller meant, whichever field they used.</summary>
    public string? DestinationName =>
        string.IsNullOrWhiteSpace(Destination) ? Target : Destination;
}

/// <summary>
/// What became of a <see cref="PrintRequest"/>. <see cref="Success"/> is about
/// the spooler accepting the job — no printer tells you the customer took the
/// receipt.
/// </summary>
public sealed record PrintResponse(
    string JobId,
    bool Success,
    string? Destination = null,
    string? Queue = null,
    string? Error = null);

/// <summary>
/// Unauthenticated liveness probe. <see cref="AuthRequired"/> lets a caller
/// discover whether it needs a key before it starts guessing why its jobs are
/// being refused.
/// </summary>
public sealed record HealthResponse(string Status, string Station, string Version, bool AuthRequired);

/// <summary>
/// One addressable destination. <see cref="DotWidth"/> and <see cref="RenderDpi"/>
/// are advertised so a caller renders at the right size instead of hard-coding
/// it — the printer's dimensions belong to the printer, not to the POS.
/// </summary>
public sealed record DestinationInfo(
    string Name,
    string Kind,
    string ContentType,
    string? Language,
    string Queue,
    int DotWidth,
    int RenderDpi,
    bool Installed,
    bool Enabled);

public sealed record DestinationsResponse(
    string Station,
    IReadOnlyList<DestinationInfo> Destinations,
    IReadOnlyList<string> Queues);
