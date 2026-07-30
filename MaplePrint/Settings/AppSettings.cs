using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace MaplePrint.Settings;

/// <summary>
/// Per-machine MaplePrint configuration. Persisted as JSON at
/// <c>%APPDATA%\MaplePrint\settings.json</c>, so it survives upgrades (the app
/// ships as a bare .exe with no installer).
///
/// Note what is *not* here: any mention of MapleCoffee, or of whoever else
/// sends jobs. The print server knows about printers and nothing else — callers
/// are configured with our address, not the other way round.
/// </summary>
public sealed class AppSettings
{
    /// <summary>TCP port the local print API listens on.</summary>
    public int ListenPort { get; set; } = DefaultPort;

    /// <summary>
    /// Shared secret a caller must present. **Blank by default, which disables
    /// authentication entirely** — the normal deployment is a shop LAN where
    /// every caller is already trusted, and a key there is friction without a
    /// threat. Set one when the port is exposed beyond the LAN (a tunnel, a
    /// port forward), where "anyone who can route to us may print" stops being
    /// an acceptable sentence.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// False binds to loopback only — nothing off this PC can reach the API.
    /// True binds all interfaces so another machine (the POS server, a kiosk)
    /// can send jobs. Off by default: opening a port is the operator's call.
    /// </summary>
    public bool AllowRemote { get; set; }

    /// <summary>
    /// Identifies this PC in health responses and the activity log, so a shop
    /// with two counters can tell them apart.
    /// </summary>
    public string StationName { get; set; } = Environment.MachineName;

    /// <summary>The names callers may address, and what each one maps to here.</summary>
    public List<Destination> Destinations { get; set; } = [];

    /// <summary>Launch on sign-in by registering under HKCU\...\Run.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Per-window size and position, keyed by form name.</summary>
    public Dictionary<string, WindowPlacement> WindowPlacements { get; set; } = new();

    // ---- Legacy (pre-destinations) fields -------------------------------
    // Read so an existing install migrates instead of silently losing its
    // printer mapping. Nulled once migrated, and WhenWritingNull keeps them
    // from being written back, so the file cleans itself up on next save.

    [JsonPropertyName("receiptQueue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyReceiptQueue { get; set; }

    [JsonPropertyName("labelQueue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyLabelQueue { get; set; }

    /// <summary>
    /// 9110 rather than something in the 9100 block: 9100 is JetDirect itself,
    /// and a receipt printer on the same LAN may well be using it.
    /// </summary>
    public const int DefaultPort = 9110;

    // JsonIgnore on both: System.Text.Json serializes get-only properties, so
    // without this these computed values were being written into settings.json
    // as if they were settings, and read back as noise.

    /// <summary>True once we have a usable port. A key is not required to serve.</summary>
    [JsonIgnore]
    public bool IsServable => ListenPort is > 0 and < 65536;

    /// <summary>False when <see cref="ApiKey"/> is blank — every caller is allowed.</summary>
    [JsonIgnore]
    public bool RequiresAuth => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Case-insensitive, because callers type these by hand.</summary>
    public Destination? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Destinations.FirstOrDefault(d =>
                string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Brings a settings file up to the destinations model. Returns true if
    /// anything changed, so the caller can persist the cleaned-up form.
    /// </summary>
    public bool Migrate()
    {
        Destinations ??= [];

        var changed = false;

        if (Destinations.Count == 0)
        {
            Destinations.Add(new Destination
            {
                Name = DestinationNames.Receipt,
                // The Star TSP100 family is raster-only — it has no command
                // language — so the default for receipts is Document, not Raw.
                Kind = DestinationKind.Document,
                Queue = (LegacyReceiptQueue ?? "").Trim(),
                DotWidth = DotWidths.Receipt80mm
            });

            Destinations.Add(new Destination
            {
                Name = DestinationNames.Label,
                // Document rather than raw ZPL. Native ZPL barcode elements are
                // crisper, but they only cover what the printer's firmware knows
                // — and a renderer that can't emit ^BQ can't produce a QR code
                // at all. Sending a rendered page sidesteps the command set:
                // whatever the caller can draw, the printer can print.
                Kind = DestinationKind.Document,
                Queue = (LegacyLabelQueue ?? "").Trim(),
                DotWidth = DotWidths.Label2Inch
            });

            changed = true;
        }

        if (LegacyReceiptQueue is not null || LegacyLabelQueue is not null)
        {
            LegacyReceiptQueue = null;
            LegacyLabelQueue = null;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 32 bytes of CSPRNG output, hex-encoded. Long enough that the key is not
    /// worth guessing over a LAN, short enough to paste without wrapping.
    /// </summary>
    public static string NewApiKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
