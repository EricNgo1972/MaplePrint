namespace MaplePrint.Cards;

/// <summary>
/// What kind of chip is on the reader, as far as the reader can tell from its answer to reset
/// (ATR). Which kinds a shop uses is the shop's decision; this only says what the card in hand is,
/// so the person at the counter can be told what it will and won't do.
/// </summary>
public enum NfcCardKind
{
    /// <summary>The reader answered with something this library does not recognise.</summary>
    Unknown,

    /// <summary>
    /// MIFARE Classic 1K ("S50"). Read by its UID only: its memory needs sector keys, phones can't
    /// read it, and nothing is ever written to one.
    /// </summary>
    MifareClassic1K,

    /// <summary>MIFARE Classic 4K ("S70"). As 1K.</summary>
    MifareClassic4K,

    /// <summary>MIFARE Mini. As 1K.</summary>
    MifareMini,

    /// <summary>
    /// An NFC Forum Type 2 tag: NTAG213/215/216 and MIFARE Ultralight all report as this. Its
    /// memory is open pages, it can hold an NDEF link, and an Android phone reads it too.
    /// </summary>
    Ultralight,

    /// <summary>
    /// An ISO 14443-4 card that speaks APDUs itself: DESFire, a bank card, a phone's wallet. Read
    /// by its UID only, and a phone's UID changes on every tap, so it can't be a card's key.
    /// </summary>
    IsoDep,
}

/// <summary>Reads the card's kind from the ATR a PC/SC contactless reader builds for it.</summary>
public static class NfcAtr
{
    /// <summary>
    /// The kind of card behind an ATR.
    /// </summary>
    /// <remarks>
    /// A storage card (MIFARE Classic, Ultralight) gets the PC/SC Part 3 ATR
    /// <c>3B 8F 80 01 80 4F 0C A0 00 00 03 06 SS NN NN 00 00 00 00 TCK</c>, where <c>SS</c> is the
    /// standard and <c>NN NN</c> names the card. Anything else with the contactless prefix
    /// <c>3B 8x 80 01</c> is a card that speaks ISO 14443-4 itself.
    /// </remarks>
    public static NfcCardKind KindOf(ReadOnlySpan<byte> atr)
    {
        if (atr.Length < 4 || atr[0] != 0x3B || (atr[1] & 0xF0) != 0x80 || atr[2] != 0x80 || atr[3] != 0x01)
            return NfcCardKind.Unknown;

        var isStorageCard = atr.Length >= 15 && atr[4] == 0x80 && atr[5] == 0x4F
                            && atr[7] == 0xA0 && atr[8] == 0x00 && atr[9] == 0x00 && atr[10] == 0x03 && atr[11] == 0x06;
        if (!isStorageCard) return NfcCardKind.IsoDep;

        return ((atr[13] << 8) | atr[14]) switch
        {
            0x0001 => NfcCardKind.MifareClassic1K,
            0x0002 => NfcCardKind.MifareClassic4K,
            0x0003 => NfcCardKind.Ultralight,
            0x0026 => NfcCardKind.MifareMini,
            _ => NfcCardKind.Unknown,
        };
    }

    /// <summary>What staff call it.</summary>
    public static string Describe(NfcCardKind kind) => kind switch
    {
        NfcCardKind.MifareClassic1K => "MIFARE Classic 1K",
        NfcCardKind.MifareClassic4K => "MIFARE Classic 4K",
        NfcCardKind.MifareMini => "MIFARE Mini",
        NfcCardKind.Ultralight => "NTAG / MIFARE Ultralight",
        NfcCardKind.IsoDep => "a smart card or phone (ISO 14443-4)",
        _ => "an unknown card",
    };
}
