using System.Text.Json.Serialization;

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
/// <param name="OpenDrawer">
/// Fire the cash drawer on this destination's printer as the job goes out.
/// Absent means false, so a caller written before drawers existed behaves
/// exactly as it always did. With an empty payload this is a no-sale: the
/// drawer opens and nothing prints.
/// </param>
/// <param name="Reason">
/// What this open was for — <c>sale:1042</c>, <c>no-sale:e.ngo</c>,
/// <c>reprint:1042</c>. Free text, recorded verbatim in the drawer audit.
/// </param>
/// <param name="User">
/// Who the caller says asked for it. Recorded, never verified: MaplePrint has
/// no user accounts and no roles. Whether this person is signed in, and whether
/// they may open a till without a sale, is the POS's decision and is made before
/// we are called.
/// </param>
public sealed record PrintRequest(
    string? Destination = null,
    string? ContentType = null,
    string? PayloadBase64 = null,
    string? Description = null,
    string? JobId = null,
    int Copies = 1,
    string? Target = null,
    bool OpenDrawer = false,
    string? Reason = null,
    string? User = null)
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
/// <param name="DrawerOpened">True if the drawer was actually kicked for this
/// job. False when none was asked for, and when a retry's kick was suppressed.</param>
/// <param name="Retryable">
/// False on the one failure that must not be sent again automatically: a
/// drawer-only job. Absent otherwise, which leaves the existing rule — 502 and
/// 503 are worth retrying, 4xx is not. Advisory: a caller that decides from the
/// status code alone is not wrong, it just cannot tell this case apart.
/// </param>
public sealed record PrintResponse(
    string JobId,
    bool Success,
    string? Destination = null,
    string? Queue = null,
    string? Error = null,
    bool DrawerOpened = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Retryable = null);

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
/// <param name="Drawer">Null when no drawer is wired to this printer, which is
/// every destination until somebody sets one up.</param>
public sealed record DestinationInfo(
    string Name,
    string Kind,
    string ContentType,
    string? Language,
    string Queue,
    int DotWidth,
    int RenderDpi,
    bool Installed,
    bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DrawerInfo? Drawer = null);

/// <summary>
/// What a caller can discover about a destination's cash drawer, so a POS finds
/// out at integration time whether this counter has one — rather than at the
/// counter, from a drawer that does not move.
/// </summary>
public sealed record DrawerInfo(string CommandSet, int PulseMs, int Unit);

public sealed record DestinationsResponse(
    string Station,
    IReadOnlyList<DestinationInfo> Destinations,
    IReadOnlyList<string> Queues);
