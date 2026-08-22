namespace MaplePrint.Printing;

/// <summary>
/// Remembers which jobs have already opened the drawer, so a retry reprints
/// without opening it a second time.
///
/// <para>Print and drawer retries are not the same thing. A re-printed receipt
/// wastes a slip; a re-kicked drawer is a till standing open with nobody in
/// front of it, which for a merchant is a loss-prevention incident. So the
/// payload may be resent freely and the kick may not.</para>
///
/// <para>Keyed on the caller's own job id, because that is the only thing that
/// survives a retry — the caller times out, sends the same job again, and this
/// is what tells the two attempts apart from two real sales. A job id we minted
/// ourselves is never recorded: it is unique per request by construction, so it
/// could only ever fill the ledger without ever matching.</para>
///
/// <para>Claimed <i>before</i> the write is attempted, deliberately. A kick that
/// succeeded and a payload that then failed is a drawer that opened, and the
/// retry must not open it again — so a partial write counts as already-opened,
/// which is the safe direction to be wrong in.</para>
/// </summary>
public sealed class DrawerKickLedger
{
    /// <summary>
    /// Long enough to cover a caller giving up and retrying — the shipped client
    /// waits 5 seconds and MaplePrint may still be spooling a minute later —
    /// short enough that a genuine second sale on the same till is never mistaken
    /// for a retry. Callers that reuse one job id across real sales would be
    /// broken in more visible ways first.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    /// <summary>Bounded: this process runs for months on a counter PC.</summary>
    private const int Capacity = 500;

    private readonly TimeSpan _window;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _kicked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();

    public DrawerKickLedger(TimeSpan? window = null, Func<DateTime>? now = null)
    {
        _window = window ?? DefaultWindow;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// True if this job may open the drawer, and records that it did. False if
    /// the same job id already opened it inside the window — the caller then
    /// prints without kicking.
    /// </summary>
    public bool TryClaim(string? jobId)
    {
        // No id from the caller means no way to recognise the retry, so there is
        // nothing to claim and nothing to suppress.
        if (string.IsNullOrWhiteSpace(jobId)) return true;

        var key = jobId.Trim();
        var now = _now();

        lock (_gate)
        {
            if (_kicked.TryGetValue(key, out var at) && now - at < _window)
                return false;

            // Enqueued only when the id is new, so the queue holds exactly one
            // entry per key and eviction stays honest. A re-claim after the
            // window has passed just restamps the entry already in there.
            if (!_kicked.ContainsKey(key)) _order.Enqueue(key);
            _kicked[key] = now;

            while (_order.Count > Capacity)
                _kicked.Remove(_order.Dequeue());

            return true;
        }
    }

    /// <summary>True if this job has already opened the drawer. Does not claim.</summary>
    public bool AlreadyKicked(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return false;

        lock (_gate)
            return _kicked.TryGetValue(jobId.Trim(), out var at) && _now() - at < _window;
    }
}
