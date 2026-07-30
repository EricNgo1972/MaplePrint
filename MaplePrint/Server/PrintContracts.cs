namespace MaplePrint.Server;

/// <summary>
/// Logical destinations. A caller asks for a *role*, not a Windows queue name,
/// so the shop can rename or replace a printer without the calling app being
/// touched. Anything that isn't a known role is treated as a literal queue
/// name, which lets a caller reach a third printer we haven't named yet.
/// </summary>
public static class PrintTargets
{
    public const string Receipt = "receipt";
    public const string Label = "label";
}

/// <summary>
/// A job. <see cref="PayloadBase64"/> is the device bytes — ESC/POS for a
/// receipt, ZPL for a label — encoded by whoever is calling. MaplePrint never
/// inspects, reformats or validates them; it is a paper path, not a renderer.
/// </summary>
public sealed record PrintRequest(
    string? Target,
    string? PayloadBase64,
    string? Description = null,
    string? JobId = null);

/// <summary>
/// The outcome. <see cref="Success"/> is about the spooler accepting the job —
/// no printer tells you the customer took the receipt.
/// </summary>
public sealed record PrintResponse(
    string JobId,
    bool Success,
    string? Queue = null,
    string? Error = null);

/// <summary>Unauthenticated liveness probe: is something listening, and what is it?</summary>
public sealed record HealthResponse(string Status, string Station, string Version);

/// <summary>What this station can print to, and which queue currently holds each role.</summary>
public sealed record PrintersResponse(
    string Station,
    IReadOnlyList<string> Printers,
    string? ReceiptQueue,
    string? LabelQueue);
