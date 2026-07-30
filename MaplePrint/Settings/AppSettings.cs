using System.Security.Cryptography;

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
    /// Shared secret a caller must present. Generated on first run and copied
    /// out of the settings dialog into whatever app is going to print.
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

    /// <summary>Windows queue serving the <c>receipt</c> role — the ESC/POS thermal printer.</summary>
    public string ReceiptQueue { get; set; } = "";

    /// <summary>Windows queue serving the <c>label</c> role — the ZPL label printer.</summary>
    public string LabelQueue { get; set; } = "";

    /// <summary>Launch on sign-in by registering under HKCU\...\Run.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// 9110 rather than something in the 9100 block: 9100 is JetDirect itself,
    /// and a receipt printer on the same LAN may well be using it.
    /// </summary>
    public const int DefaultPort = 9110;

    /// <summary>True once there is a key and a usable port — i.e. we can serve.</summary>
    public bool IsServable => !string.IsNullOrWhiteSpace(ApiKey) && ListenPort is > 0 and < 65536;

    /// <summary>
    /// 32 bytes of CSPRNG output, hex-encoded. Long enough that the key is not
    /// worth guessing over a LAN, short enough to paste without wrapping.
    /// </summary>
    public static string NewApiKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
