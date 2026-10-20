using MaplePrint.Cards;

namespace MaplePrint.Tests.Cards;

/// <summary>
/// An NTAG on a PC/SC reader, in memory: answers GET DATA, READ BINARY and UPDATE BINARY the way
/// the ACR122U does, and records every page written. The record is the point — the tests are
/// about which pages a card is allowed to have touched.
/// </summary>
internal sealed class FakeType2Tag
{
    // The ATR an ACR122U builds for an NTAG / Ultralight.
    public static readonly byte[] UltralightAtr =
        [0x3B, 0x8F, 0x80, 0x01, 0x80, 0x4F, 0x0C, 0xA0, 0x00, 0x00, 0x03, 0x06, 0x03, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x68];

    // The ATR it built for the MIFARE Classic 1K on the test bench.
    public static readonly byte[] Classic1KAtr =
        [0x3B, 0x8F, 0x80, 0x01, 0x80, 0x4F, 0x0C, 0xA0, 0x00, 0x00, 0x03, 0x06, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x6A];

    public static readonly byte[] Uid = [0x04, 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6];

    private readonly byte[] _pages;

    /// <summary>Every page number written, in order.</summary>
    public List<int> Writes { get; } = [];

    /// <summary>When set, the card leaves the reader after this many more writes.</summary>
    public int? RemovedAfterWrites { get; set; }

    /// <summary>
    /// A factory-fresh NTAG213: 45 pages, capability container <c>E1 10 12 00</c> (144 bytes of
    /// NDEF, read/write) and an empty NDEF message.
    /// </summary>
    public static FakeType2Tag Ntag213(byte access = 0x00, byte sizeUnits = 0x12, byte magic = 0xE1)
    {
        var tag = new FakeType2Tag(45);
        Uid.AsSpan(0, 3).CopyTo(tag._pages.AsSpan(0));
        Uid.AsSpan(3, 4).CopyTo(tag._pages.AsSpan(4));
        tag._pages[12] = magic;
        tag._pages[13] = 0x10;
        tag._pages[14] = sizeUnits;
        tag._pages[15] = access;
        tag._pages[16] = 0x03; // NDEF TLV, empty
        tag._pages[17] = 0x00;
        tag._pages[18] = 0xFE;
        return tag;
    }

    private FakeType2Tag(int pageCount) => _pages = new byte[pageCount * 4];

    public byte[] Page(int page) => _pages.AsSpan(page * 4, 4).ToArray();

    public void SetData(int offset, params byte[] bytes) => bytes.CopyTo(_pages, 16 + offset);

    public NfcCard Connect(byte[]? atr = null) => new(Transmit, atr ?? UltralightAtr);

    private byte[] Transmit(byte[] apdu)
    {
        if (RemovedAfterWrites == 0) throw new NfcException("The card was lifted off the reader.");

        switch (apdu)
        {
            case [0xFF, 0xCA, 0x00, 0x00, 0x00]:
                return [.. Uid, 0x90, 0x00];

            case [0xFF, 0xB0, 0x00, var page, 0x10]:
                // An NTAG READ wraps round past the last page.
                var read = Enumerable.Range(0, 16).Select(i => _pages[(page * 4 + i) % _pages.Length]);
                return [.. read, 0x90, 0x00];

            case [0xFF, 0xD6, 0x00, var page, 0x04, ..] when apdu.Length == 9:
                if (page * 4 >= _pages.Length) return [0x63, 0x00];
                apdu.AsSpan(5, 4).CopyTo(_pages.AsSpan(page * 4));
                Writes.Add(page);
                if (RemovedAfterWrites is > 0) RemovedAfterWrites--;
                return [0x90, 0x00];

            default:
                return [0x6A, 0x81];
        }
    }
}
