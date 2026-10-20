namespace MaplePrint.Cards;

/// <summary>
/// A card on the reader, connected. Every card has a UID; an NTAG or Ultralight (a Type 2 tag)
/// also has open pages that can hold an NDEF link. Dispose it to let go of the card.
///
/// <para><b>What is never written.</b> Pages 0–3 (the UID, the lock bytes and the capability
/// container) and anything past the data area the capability container declares (the dynamic lock
/// bytes and configuration pages of an NTAG). Lock bits are one-way: a wrong byte there turns a
/// card into plastic for good, so no call here can reach them, whatever its arguments.</para>
///
/// <para>Only the PC/SC Part 3 commands every contactless reader implements are sent (GET DATA,
/// READ BINARY, UPDATE BINARY), so a reader other than the ACR122U works unchanged.</para>
/// </summary>
public sealed class NfcCard : IDisposable
{
    private const int PageSize = 4;
    private const int FirstDataPage = 4;

    private readonly Func<byte[], byte[]> _transmit;
    private readonly Action? _release;
    private TagMemory? _memory;

    /// <param name="transmit">Sends one APDU and returns the whole response, status word included.</param>
    /// <param name="atr">The reader's answer to reset for this card.</param>
    /// <param name="release">Called once on dispose.</param>
    internal NfcCard(Func<byte[], byte[]> transmit, byte[] atr, Action? release = null)
    {
        _transmit = transmit;
        _release = release;
        Atr = atr;
        Kind = NfcAtr.KindOf(atr);
        UidBytes = Send([0xFF, 0xCA, 0x00, 0x00, 0x00], "read the card's ID");
    }

    public byte[] Atr { get; }

    public NfcCardKind Kind { get; }

    /// <summary>The chip's ID as the reader sends it.</summary>
    public byte[] UidBytes { get; }

    /// <summary>
    /// The chip's ID in uppercase hex, no separators (<c>04A1B2C3D4E5F6</c>), in the byte order the
    /// reader gives it. What the ID means is the caller's business.
    /// </summary>
    public string Uid => Convert.ToHexString(UidBytes);

    /// <summary>Whether this card has pages that can hold an NDEF link (NTAG, Ultralight).</summary>
    public bool HasPages => Kind == NfcCardKind.Ultralight;

    /// <summary>
    /// Bytes available for NDEF, as the card's capability container declares (144 on an NTAG213,
    /// 496 on an NTAG215, 872 on an NTAG216, 48 on an Ultralight). 0 when the card has no pages or
    /// was never formatted for NDEF.
    /// </summary>
    public int Capacity => Memory().DataBytes;

    /// <summary>
    /// Whether NDEF can be written: formatted, and its capability container doesn't say read-only.
    /// </summary>
    public bool IsWritable => Memory().Writable;

    /// <summary>The whole data area, page 4 to the end of the NDEF capacity.</summary>
    public byte[] ReadDataArea()
    {
        var memory = RequireFormatted();
        var area = new byte[memory.DataBytes];
        for (var offset = 0; offset < area.Length; offset += 16)
        {
            // READ BINARY returns four pages at a time.
            var chunk = ReadPages(FirstDataPage + offset / PageSize);
            Array.Copy(chunk, 0, area, offset, Math.Min(16, area.Length - offset));
        }
        return area;
    }

    /// <summary>
    /// Writes <paramref name="data"/> to the start of the data area, then reads it back. Only the
    /// pages whose bytes differ are written, and the last page keeps whatever the card held past
    /// the end of <paramref name="data"/>.
    /// </summary>
    /// <returns>How many pages were written.</returns>
    /// <exception cref="NfcException">
    /// The card can't take it (no pages, not formatted, read-only, too small), the card left the
    /// reader, or what was read back is not what was written.
    /// </exception>
    public int WriteDataArea(ReadOnlySpan<byte> data)
    {
        var memory = RequireFormatted();
        if (!memory.Writable) throw new NfcException("This card is locked read-only, so nothing can be written to it.");
        if (data.Length > memory.DataBytes)
            throw new NfcException($"That needs {data.Length} bytes and this card holds {memory.DataBytes}.");

        var before = ReadDataArea();
        var after = (byte[])before.Clone();
        data.CopyTo(after);

        var written = 0;
        for (var offset = 0; offset < data.Length; offset += PageSize)
        {
            if (after.AsSpan(offset, PageSize).SequenceEqual(before.AsSpan(offset, PageSize))) continue;
            WritePage(FirstDataPage + offset / PageSize, after.AsSpan(offset, PageSize).ToArray());
            written++;
        }

        if (!ReadDataArea().AsSpan(0, data.Length).SequenceEqual(data))
            throw new NfcException("The card did not keep what was written. Hold it still on the reader and try again.");
        return written;
    }

    /// <summary>The NDEF records on the card. Empty when it holds none or has no pages.</summary>
    public IReadOnlyList<NdefRecord> ReadNdef()
    {
        if (!HasPages || !Memory().Formatted) return [];
        var (_, message) = Ndef.FindMessage(ReadDataArea());
        return message is null ? [] : Ndef.Decode(message);
    }

    /// <summary>
    /// Replaces the NDEF message on the card with these records and checks it by reading it back.
    /// Lock and memory control TLVs before the message are left where they are.
    /// </summary>
    /// <exception cref="NfcException">As <see cref="WriteDataArea"/>.</exception>
    public void WriteNdef(IReadOnlyList<NdefRecord> records)
    {
        var area = ReadDataArea();
        var (offset, _) = Ndef.FindMessage(area);
        var tlv = Ndef.WrapTlv(Ndef.Encode(records));
        if (offset + tlv.Length > area.Length)
            throw new NfcException($"That needs {offset + tlv.Length} bytes and this card holds {area.Length}.");

        var data = area.AsSpan(0, offset + tlv.Length).ToArray();
        tlv.CopyTo(data, offset);
        WriteDataArea(data);
    }

    public void Dispose() => _release?.Invoke();

    // ── Pages ────────────────────────────────────────────────────────────────

    private byte[] ReadPages(int page)
    {
        var data = Send([0xFF, 0xB0, 0x00, (byte)page, 0x10], $"read page {page}");
        if (data.Length < 16) throw new NfcException($"The card answered page {page} with {data.Length} bytes instead of 16.");
        return data;
    }

    private void WritePage(int page, byte[] bytes)
    {
        // The guard every write passes, so that no caller can reach a lock or configuration page.
        var memory = Memory();
        if (page < FirstDataPage || page >= FirstDataPage + memory.DataBytes / PageSize || bytes.Length != PageSize)
            throw new InvalidOperationException($"Refusing to write page {page}: outside the card's data area.");

        Send([0xFF, 0xD6, 0x00, (byte)page, PageSize, .. bytes], $"write page {page}");
    }

    private TagMemory Memory()
    {
        if (_memory is { } known) return known;
        if (!HasPages) return _memory = TagMemory.None;

        // Page 3 is the capability container: E1, version, data size in 8-byte units, access.
        // A page number is one byte, so a size past page 255 is a card misreporting itself.
        var cc = ReadPages(3);
        return _memory = cc[0] == 0xE1
            ? new TagMemory(true, Math.Min(cc[2] * 8, (256 - FirstDataPage) * PageSize), Writable: cc[3] == 0x00)
            : TagMemory.None;
    }

    private TagMemory RequireFormatted()
    {
        if (!HasPages)
            throw new NfcException($"This is {NfcAtr.Describe(Kind)}. It is read by its ID only; nothing is written to it.");
        var memory = Memory();
        // Formatting writes the capability container, which is one-time-programmable: never done here.
        if (!memory.Formatted) throw new NfcException("This card was never set up for NDEF, so nothing can be written to it.");
        return memory;
    }

    private byte[] Send(byte[] apdu, string what)
    {
        var response = _transmit(apdu);
        if (response.Length < 2) throw new NfcException($"The reader gave no answer when asked to {what}.");

        var sw1 = response[^2];
        var sw2 = response[^1];
        if (sw1 != 0x90 || sw2 != 0x00)
            throw new NfcException($"The card refused to {what} (status {sw1:X2}{sw2:X2}). Hold it still on the reader and try again.", (sw1 << 8) | sw2);
        return response[..^2];
    }

    private sealed record TagMemory(bool Formatted, int DataBytes, bool Writable)
    {
        /// <summary>No pages, or pages never formatted for NDEF.</summary>
        public static readonly TagMemory None = new(false, 0, false);
    }
}
