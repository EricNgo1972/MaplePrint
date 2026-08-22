using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaplePrint.Diagnostics;

/// <summary>
/// One drawer event, as written to the audit file. One JSON object per line —
/// readable in a text editor, and loadable by anything that can read NDJSON
/// without a parser being written for it.
/// </summary>
/// <param name="At">Local time with offset, so a shift boundary still means what it meant.</param>
/// <param name="Reason">Whatever the caller said this was for — <c>sale:1042</c>,
/// <c>no-sale:e.ngo</c>, <c>reprint:1042</c>. Free text; recorded verbatim.</param>
/// <param name="User">Who the caller says asked for it. Recorded, never verified —
/// MaplePrint has no identities.</param>
/// <param name="Outcome"><c>opened</c>, <c>suppressed</c>, <c>failed</c> or <c>refused</c>.</param>
public sealed record DrawerAuditEntry(
    DateTimeOffset At,
    string Station,
    string Destination,
    string Queue,
    string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommandSet = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PulseMs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Unit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? User = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? JobId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

/// <summary>
/// The append-only record of every time this station opened, or tried to open, a
/// cash drawer.
///
/// <para>Separate from <see cref="ActivityLog"/> on purpose. That log rolls at
/// 1 MB and keeps one previous file, so the older half is deleted — fine for
/// "why didn't a receipt come out this morning", useless for "no-sale opens by
/// employee, by shift, last quarter", which is what a merchant eventually asks
/// for. Nothing here is ever deleted; a full file is archived under a dated name
/// and a new one started.</para>
///
/// <para><b>This is a record, not a gate.</b> Whether a cashier is signed in, and
/// whether they are allowed to open a till without a sale, is the POS's decision
/// and is made before MaplePrint is called. <see cref="DrawerAuditEntry.User"/>
/// is a string a caller sent us; we write down what we were told.</para>
/// </summary>
public sealed class DrawerAudit
{
    /// <summary>Archived, not deleted, at this size.</summary>
    private const long RollAtBytes = 1_000_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _fileLock = new();
    private readonly ActivityLog? _activity;

    public DrawerAudit(string folder, ActivityLog? activity = null)
    {
        FilePath = Path.Combine(folder, "drawer.log");
        _activity = activity;
    }

    public string FilePath { get; }

    public const string Opened = "opened";
    public const string Suppressed = "suppressed";
    public const string Failed = "failed";
    public const string Refused = "refused";

    /// <summary>
    /// Writes one line. Never throws — losing the drawer itself to a failed log
    /// write would be the worse fault — but unlike the activity log, a failure
    /// here is reported, because a silently unwritten audit trail is exactly the
    /// thing somebody will later rely on.
    /// </summary>
    public void Write(DrawerAuditEntry entry)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > RollAtBytes)
                {
                    // Archived under a dated name and kept. The merchant's
                    // records are not ours to prune.
                    var archive = Path.Combine(
                        Path.GetDirectoryName(FilePath)!,
                        $"drawer-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    if (!File.Exists(archive)) File.Move(FilePath, archive);
                }

                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _activity?.Error($"Could not write the drawer audit line: {ex.Message}");
        }
    }
}
