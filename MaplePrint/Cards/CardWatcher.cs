using System.Runtime.Versioning;
using MaplePrint.Diagnostics;
using MaplePrint.Settings;

namespace MaplePrint.Cards;

/// <summary>
/// Watches the USB card reader (an ACR122U, or any PC/SC contactless reader) and sends the ID of
/// each card laid on it to the Maple shop set in Settings. The shop hands it to every counter
/// screen this reader is attached to — on this PC, a tablet at the till, anywhere — so the screen
/// never has to be on the PC the reader is plugged into.
///
/// <para>Two loops, independent on purpose. Checking in every 15 seconds keeps the shop's setup page
/// showing the reader as online and learns about pairing; reading keeps going while the shop can't
/// be reached, so the person at the counter is told straight away that a card didn't get through,
/// rather than nothing happening.</para>
///
/// <para>One tap is one read: a card must be lifted off before it counts again, so a card left
/// lying on the reader is sent once, not every second. A reader that is unplugged is looked for
/// again every few seconds, and plugging it back in needs nobody to touch MaplePrint.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CardWatcher : IDisposable
{
    // The shop counts a reader as online for 45 seconds after it checks in (PhoneReaderHub.OnlineWindow).
    private static readonly TimeSpan CheckInEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReaderRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WaitSlice = TimeSpan.FromSeconds(30);

    internal enum Shop { NotSet, Reaching, Unreachable, NotPaired, Paired }

    private readonly ActivityLog _log;
    private readonly ShopClient _client = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _gate = new();
    private volatile AppSettings _settings;
    private volatile bool _paused;
    private Task? _loops;

    // What the status line is made from; written by both loops, under _gate.
    private string? _readerName;
    private Shop _shop = Shop.Reaching;
    private string? _pairCode;
    private string? _pairedAs;

    public CardWatcher(AppSettings settings, ActivityLog log)
    {
        _settings = settings;
        _log = log;
        if (!settings.HasCardShop) _shop = Shop.NotSet;
    }

    /// <summary>One line for the tray menu: what someone at the counter needs to know about the reader.</summary>
    public string Status { get; private set; } = "Card reader: looking…";

    /// <summary>Raised from the watcher's own threads. Marshal to the UI thread before touching controls.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// A card was read but no counter screen got it — not paired, nobody waiting, shop unreachable.
    /// For a balloon: the person who tapped it is looking at a screen where nothing happened, and
    /// nobody at a counter reads the activity log. Raised from the watcher's own threads.
    /// </summary>
    public event Action<string>? Missed;

    public void Start() => _loops ??= Task.WhenAll(
        Task.Run(() => CheckInAsync(_stop.Token)),
        Task.Run(() => ReadAsync(_stop.Token)));

    /// <summary>Takes effect at once: a new shop or a new key checks in straight away.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _wake.Release();
    }

    /// <summary>
    /// Stops sending cards to the shop until the result is disposed — while the card reader check
    /// has the reader, so its test taps don't land on a counter screen as a customer's card.
    /// </summary>
    public IDisposable Pause()
    {
        _paused = true;
        Publish();
        return new Resume(this);
    }

    /// <summary>
    /// A card's ID for the activity log and the tray: enough to tell two cards apart, not the whole
    /// ID. A card's ID can be what a gift card is spent by, and the log sits on a counter PC.
    /// </summary>
    public static string Masked(string uid) => uid.Length <= 4 ? uid : $"{uid[..2]}…{uid[^2..]}";

    public void Dispose()
    {
        _stop.Cancel();
        // Bounded: a reader wedged in a driver call must not hold Exit open (see TrayApplicationContext).
        try { _loops?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
        _client.Dispose();
    }

    // ---- Checking in ------------------------------------------------------

    private async Task CheckInAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var settings = _settings;
            if (!settings.HasCardShop) SetShop(Shop.NotSet);
            else
            {
                try
                {
                    var hello = await _client.HelloAsync(settings.CardShopUrl, settings.CardDeviceKey, ct);
                    SetShop(hello.Paired ? Shop.Paired : Shop.NotPaired, hello.PairCode, hello.ReaderName);
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    SetShop(Shop.Unreachable, reason: $"{settings.CardShopUrl}: {ShopClient.Reason(ex)}");
                }
                catch (OperationCanceledException) { return; }
            }

            try { await _wake.WaitAsync(CheckInEvery, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetShop(Shop shop, string? pairCode = null, string? pairedAs = null, string? reason = null)
    {
        bool changed;
        lock (_gate)
        {
            changed = shop != _shop || pairCode != _pairCode || pairedAs != _pairedAs;
            (_shop, _pairCode, _pairedAs) = (shop, pairCode, pairedAs);
        }

        // Published whether or not it changed: the first answer replaces "looking…" even when it is
        // the state the watcher started in. Publish says nothing when the line is the same.
        Publish();
        if (!changed) return;

        // Said once, when it changes: the log is for what happened, not a heartbeat.
        switch (shop)
        {
            case Shop.Paired: _log.Success($"Card reader: paired with the shop as \"{pairedAs}\"."); break;
            case Shop.NotPaired: _log.Info($"Card reader: not paired yet. Enter the code {pairCode} on the shop's card readers setup page."); break;
            case Shop.Unreachable: _log.Error($"Card reader: can't reach the shop at {reason}."); break;
        }
    }

    // ---- Reading ----------------------------------------------------------

    private async Task ReadAsync(CancellationToken ct)
    {
        string? lastProblem = null;
        while (!ct.IsCancellationRequested)
        {
            NfcReader reader;
            try { reader = NfcReader.Open(); }
            catch (NfcException ex)
            {
                // Said once, not every five seconds: most counters have no card reader at all.
                if (ex.Message != lastProblem)
                {
                    SetReader(null);
                    _log.Info($"Card reader: {ex.Message}");
                }
                lastProblem = ex.Message;
                if (!await DelayAsync(ReaderRetry, ct)) return;
                continue;
            }

            lastProblem = null;
            SetReader(reader.Name);
            _log.Info($"Card reader ready: {reader.Name}.");
            using (reader)
            {
                try { await ReadCardsAsync(reader, ct); }
                catch (OperationCanceledException) { return; }
                catch (NfcException ex)
                {
                    // Unplugged, or the smart card service went away: open it again when it's back.
                    _log.Error($"Card reader: {ex.Message}");
                    SetReader(null);
                    if (!await DelayAsync(ReaderRetry, ct)) return;
                }
            }
        }
    }

    private async Task ReadCardsAsync(NfcReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string uid;
            IReadOnlyList<string> records = [];
            using (var card = await reader.WaitForCardAsync(WaitSlice, ct))
            {
                if (card is null) continue;
                uid = card.Uid;
                // Not even read while the check has the reader: it may be writing to this card.
                if (!_paused) records = TextsOn(card);
            }

            if (!_paused) await SendAsync(uid, records, ct);
            while (!await reader.WaitForRemovalAsync(WaitSlice, ct)) { }
        }
    }

    private async Task SendAsync(string uid, IReadOnlyList<string> records, CancellationToken ct)
    {
        var card = Masked(uid);
        var settings = _settings;
        if (!settings.HasCardShop)
        {
            _log.Info($"Card {card} read. No shop is set in Settings, so it went nowhere.");
            Missed?.Invoke("A card was read, but MaplePrint isn't connected to a shop. Set the shop's address in Settings → Card reader.");
            return;
        }

        try
        {
            var heard = await _client.ReadAsync(settings.CardShopUrl, settings.CardDeviceKey, uid, records, ct);
            if (heard is null)
            {
                _log.Error($"Card {card} read, but the shop says this reader isn't paired.");
                Missed?.Invoke("A card was read, but this reader isn't paired with the shop yet. Its code is in MaplePrint's menu.");
                _wake.Release(); // check in now, so the menu shows the code
            }
            else if (heard == 0)
            {
                _log.Info($"Card {card} read. No counter screen was waiting for a card.");
                Missed?.Invoke("No screen was waiting for a card. Open the card screen at the counter, then tap the card again.");
            }
            else _log.Success($"Card {card} sent to the shop; {heard} screen{(heard == 1 ? "" : "s")} heard it.");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _log.Error($"Card {card} read, but it did not reach the shop ({ShopClient.Reason(ex)}).");
            Missed?.Invoke($"The card didn't reach the shop ({ShopClient.Reason(ex)}). Tap it again.");
        }
    }

    /// <summary>The NDEF texts and links on a card with pages; none on a MIFARE Classic, or when it was lifted too soon.</summary>
    private static IReadOnlyList<string> TextsOn(NfcCard card)
    {
        if (!card.HasPages) return [];
        try { return [.. card.ReadNdef().Select(r => r.AsText()).OfType<string>()]; }
        catch (NfcException) { return []; }
    }

    private void SetReader(string? name)
    {
        lock (_gate) _readerName = name;
        Publish();
    }

    // ---- Status -------------------------------------------------------------

    private void Publish()
    {
        string status;
        lock (_gate)
        {
            status = StatusFor(_readerName, _shop, _pairCode, _pairedAs, _paused);
            if (status == Status) return;
            Status = status;
        }
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// What needs doing comes first: a code to enter, a shop that can't be reached. A PC with no
    /// reader and no shop says only that — it is every counter that never had a card reader.
    /// </summary>
    internal static string StatusFor(string? reader, Shop shop, string? pairCode, string? pairedAs, bool paused) => (reader, shop) switch
    {
        _ when paused => "Card reader: being checked — cards are not sent",
        (null, Shop.NotSet or Shop.Paired) => "Card reader: none plugged in",
        (_, Shop.NotSet) => "Card reader: no shop set (Settings → Card reader)",
        (_, Shop.Reaching) => "Card reader: reaching the shop…",
        (_, Shop.Unreachable) => "Card reader: can't reach the shop",
        (_, Shop.NotPaired) => $"Card reader: not paired — code {pairCode}",
        _ => $"Card reader: paired as \"{pairedAs}\"",
    };

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private sealed class Resume(CardWatcher watcher) : IDisposable
    {
        private CardWatcher? _watcher = watcher;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _watcher, null) is not { } w) return;
            w._paused = false;
            w.Publish();
        }
    }
}
