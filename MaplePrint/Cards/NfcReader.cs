using MaplePrint.Cards.Native;

namespace MaplePrint.Cards;

/// <summary>
/// One USB contactless reader on this PC — an ACR122U, or any PC/SC reader like it. Waits for a
/// card to be laid on it or lifted off, and connects to the card that is there.
///
/// <para>Waiting is the reader's own event (<c>SCardGetStatusChange</c>), asked in short slices so
/// a cancellation is heard within a quarter of a second. Not thread-safe: one reader is driven by
/// one loop, which is how <see cref="CardWatcher"/> uses it.</para>
/// </summary>
public sealed class NfcReader : IDisposable
{
    private const int SliceMs = 250;

    private readonly IntPtr _context;
    private int _lastState = WinSCard.StateUnaware;
    private bool _disposed;

    private NfcReader(IntPtr context, string name)
    {
        _context = context;
        Name = name;
    }

    /// <summary>What Windows calls it: "ACS ACR122U PICC Interface 0".</summary>
    public string Name { get; }

    /// <summary>The readers Windows has, by name. Empty when none is plugged in.</summary>
    /// <exception cref="NfcException">The Windows smart card service isn't running.</exception>
    public static IReadOnlyList<string> ListReaders()
    {
        var context = Establish();
        try { return ReaderNames(context); }
        finally { WinSCard.SCardReleaseContext(context); }
    }

    /// <summary>
    /// Opens the reader by name, or with null the first ACS reader (else the first reader of any
    /// make).
    /// </summary>
    /// <exception cref="NfcException">No such reader, or no reader at all.</exception>
    public static NfcReader Open(string? name = null)
    {
        var context = Establish();
        try
        {
            var names = ReaderNames(context);
            var chosen = name is not null
                ? names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                : names.FirstOrDefault(n => n.Contains("ACS", StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
            if (chosen is null)
                throw new NfcException(name is null
                    ? "No card reader is plugged in. Plug it into a USB port and wait for Windows to finish setting it up."
                    : $"The reader \"{name}\" is not plugged in.");
            return new NfcReader(context, chosen);
        }
        catch
        {
            WinSCard.SCardReleaseContext(context);
            throw;
        }
    }

    /// <summary>
    /// Waits for a card on the reader and connects to it. A card already lying there counts — it
    /// comes back at once. Null when <paramref name="timeout"/> passes with no card.
    /// </summary>
    /// <exception cref="NfcException">The reader was unplugged, or the card could not be talked to.</exception>
    public Task<NfcCard?> WaitForCardAsync(TimeSpan timeout, CancellationToken ct = default)
        => Task.Run(() => WaitFor(present: true, timeout, ct) ? Connect() : null, ct);

    /// <summary>
    /// Waits for the reader to be empty. False when <paramref name="timeout"/> passes with the card
    /// still on it. The watcher waits for this between reads, so one tap is one read.
    /// </summary>
    public Task<bool> WaitForRemovalAsync(TimeSpan timeout, CancellationToken ct = default)
        => Task.Run(() => WaitFor(present: false, timeout, ct), ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WinSCard.SCardReleaseContext(_context);
    }

    private bool WaitFor(bool present, TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var deadline = DateTime.UtcNow + timeout;
        var states = new[] { new WinSCard.ReaderState { Reader = Name, CurrentState = _lastState, Atr = new byte[36] } };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rc = WinSCard.SCardGetStatusChange(_context, SliceMs, states, 1);
            if (rc == WinSCard.Success)
            {
                _lastState = states[0].EventState & ~WinSCard.StateChanged;
                states[0].CurrentState = _lastState;
                if ((_lastState & WinSCard.StateUnavailable) != 0)
                    throw new NfcException("The card reader was unplugged.", rc);
                if (((_lastState & WinSCard.StatePresent) != 0) == present) return true;
            }
            else if (rc == WinSCard.UnknownReader || rc == WinSCard.ReaderUnavailable || rc == WinSCard.NoReadersAvailable)
                throw new NfcException("The card reader was unplugged.", rc);
            else if (rc != WinSCard.Timeout)
                throw Failure("watch the reader", rc);

            if (DateTime.UtcNow >= deadline) return false;
        }
    }

    private NfcCard? Connect()
    {
        var rc = WinSCard.SCardConnect(_context, Name, WinSCard.ShareShared, WinSCard.ProtocolT0 | WinSCard.ProtocolT1, out var handle, out var protocol);
        // Lifted off between the reader seeing it and the connect: not there, as if it never came.
        if (rc == WinSCard.RemovedCard || rc == WinSCard.NoSmartcard) return null;
        if (rc == WinSCard.UnresponsiveCard)
            throw new NfcException("The reader sees a card but can't talk to it. It may not be a 13.56 MHz card.", rc);
        if (rc != WinSCard.Success) throw Failure("connect to the card", rc);

        var pci = new WinSCard.IoRequest { Protocol = protocol, PciLength = 8 };
        byte[] Transmit(byte[] apdu)
        {
            var response = new byte[258];
            var length = response.Length;
            var sent = WinSCard.SCardTransmit(handle, ref pci, apdu, apdu.Length, IntPtr.Zero, response, ref length);
            if (sent == WinSCard.RemovedCard)
                throw new NfcException("The card was lifted off the reader.", sent);
            if (sent != WinSCard.Success) throw Failure("talk to the card", sent);
            return response[..length];
        }

        try
        {
            return new NfcCard(Transmit, ReadAtr(), () => WinSCard.SCardDisconnect(handle, WinSCard.LeaveCard));
        }
        catch
        {
            WinSCard.SCardDisconnect(handle, WinSCard.LeaveCard);
            throw;
        }
    }

    private byte[] ReadAtr()
    {
        var states = new[] { new WinSCard.ReaderState { Reader = Name, CurrentState = WinSCard.StateUnaware, Atr = new byte[36] } };
        var rc = WinSCard.SCardGetStatusChange(_context, 0, states, 1);
        return rc == WinSCard.Success ? states[0].Atr[..Math.Min(states[0].AtrLength, 36)] : [];
    }

    private static IntPtr Establish()
    {
        var rc = WinSCard.SCardEstablishContext(WinSCard.ScopeSystem, IntPtr.Zero, IntPtr.Zero, out var context);
        if (rc == WinSCard.NoService || rc == WinSCard.ServiceStopped)
            throw new NfcException("The Windows Smart Card service is not running. Start it in Services (\"Smart Card\"), or restart the PC.", rc);
        if (rc != WinSCard.Success) throw Failure("reach the Windows smart card service", rc);
        return context;
    }

    private static IReadOnlyList<string> ReaderNames(IntPtr context)
    {
        var length = 0;
        var rc = WinSCard.SCardListReaders(context, null, null, ref length);
        if (rc == WinSCard.NoReadersAvailable) return [];
        if (rc != WinSCard.Success) throw Failure("list the card readers", rc);

        var buffer = new char[length];
        rc = WinSCard.SCardListReaders(context, null, buffer, ref length);
        if (rc == WinSCard.NoReadersAvailable) return [];
        if (rc != WinSCard.Success) throw Failure("list the card readers", rc);
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static NfcException Failure(string what, int rc)
        => new($"Could not {what} (Windows error 0x{rc:X8}).", rc);
}
