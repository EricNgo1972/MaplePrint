using System.Text;

namespace MaplePrint.Cards;

/// <summary>
/// One NDEF record: the NFC Forum's standard for what a tag holds, and what an Android phone reads
/// off it without any app. Only the two kinds a shop writes are built here — a link and a text —
/// but any record read off a card comes back, so nothing a card holds is silently dropped.
/// </summary>
/// <param name="Tnf">Type name format: 1 is an NFC Forum well-known type ("U", "T"), 3 an absolute URI.</param>
public sealed record NdefRecord(byte Tnf, byte[] Type, byte[] Payload)
{
    public const byte TnfWellKnown = 0x01;
    public const byte TnfAbsoluteUri = 0x03;

    // NFC Forum URI RTD: a link is stored as one byte naming its prefix plus the rest.
    private static readonly string[] UriPrefixes =
    [
        "", "http://www.", "https://www.", "http://", "https://", "tel:", "mailto:",
        "ftp://anonymous:anonymous@", "ftp://ftp.", "ftps://", "sftp://", "smb://", "nfs://", "ftp://",
        "dav://", "news:", "telnet://", "imap:", "rtsp://", "urn:", "pop:", "sip:", "sips:", "tftp:",
        "btspp://", "btl2cap://", "btgoep://", "tcpobex://", "irdaobex://", "file://", "urn:epc:id:",
        "urn:epc:tag:", "urn:epc:pat:", "urn:epc:raw:", "urn:epc:", "urn:nfc:",
    ];

    /// <summary>A link. A phone that taps the card opens it.</summary>
    public static NdefRecord Uri(string uri)
    {
        var prefix = 0;
        for (var i = 1; i < UriPrefixes.Length; i++)
            if (uri.StartsWith(UriPrefixes[i], StringComparison.Ordinal) && UriPrefixes[i].Length > UriPrefixes[prefix].Length)
                prefix = i;

        var rest = Encoding.UTF8.GetBytes(uri[UriPrefixes[prefix].Length..]);
        return new NdefRecord(TnfWellKnown, "U"u8.ToArray(), [(byte)prefix, .. rest]);
    }

    /// <summary>A text, in UTF-8.</summary>
    public static NdefRecord Text(string text, string language = "en")
    {
        var lang = Encoding.ASCII.GetBytes(language);
        return new NdefRecord(TnfWellKnown, "T"u8.ToArray(), [(byte)lang.Length, .. lang, .. Encoding.UTF8.GetBytes(text)]);
    }

    /// <summary>
    /// The record as a person would read it: a link in full, a text as its text — what Chrome's Web
    /// NFC hands a page, too, so a card read here and on a phone gives a caller the same string.
    /// Null for any other kind of record.
    /// </summary>
    public string? AsText()
    {
        if (Tnf == TnfAbsoluteUri) return Encoding.UTF8.GetString(Type);
        if (Tnf != TnfWellKnown || Type.Length != 1 || Payload.Length == 0) return null;

        if (Type[0] == (byte)'U')
        {
            var prefix = Payload[0] < UriPrefixes.Length ? UriPrefixes[Payload[0]] : "";
            return prefix + Encoding.UTF8.GetString(Payload, 1, Payload.Length - 1);
        }
        if (Type[0] == (byte)'T')
        {
            var status = Payload[0];
            var start = 1 + (status & 0x3F);
            if (start > Payload.Length) return null;
            var encoding = (status & 0x80) != 0 ? Encoding.BigEndianUnicode : Encoding.UTF8;
            return encoding.GetString(Payload, start, Payload.Length - start);
        }
        return null;
    }
}

/// <summary>
/// NDEF messages, and the TLV wrapper a Type 2 tag (NTAG, Ultralight) keeps one in.
/// </summary>
public static class Ndef
{
    private const byte TlvNull = 0x00;
    private const byte TlvMessage = 0x03;
    private const byte TlvTerminator = 0xFE;

    /// <summary>The message bytes for these records.</summary>
    public static byte[] Encode(IReadOnlyList<NdefRecord> records)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var shortRecord = r.Payload.Length < 256;
            var header = (byte)(r.Tnf & 0x07);
            if (i == 0) header |= 0x80;                 // MB: message begins
            if (i == records.Count - 1) header |= 0x40; // ME: message ends
            if (shortRecord) header |= 0x10;            // SR: one-byte payload length

            bytes.Add(header);
            bytes.Add((byte)r.Type.Length);
            if (shortRecord) bytes.Add((byte)r.Payload.Length);
            else bytes.AddRange([(byte)(r.Payload.Length >> 24), (byte)(r.Payload.Length >> 16), (byte)(r.Payload.Length >> 8), (byte)r.Payload.Length]);
            bytes.AddRange(r.Type);
            bytes.AddRange(r.Payload);
        }
        return [.. bytes];
    }

    /// <summary>
    /// The records in a message. Stops at the first record it can't take apart — a chunked one, or
    /// one that runs off the end — and returns what came before it: a card is data from outside,
    /// and half a message read is more use to the caller than an exception.
    /// </summary>
    public static IReadOnlyList<NdefRecord> Decode(ReadOnlySpan<byte> message)
    {
        var records = new List<NdefRecord>();
        var at = 0;
        while (at + 3 <= message.Length)
        {
            var header = message[at++];
            if ((header & 0x20) != 0) break; // CF: chunked, which no card a caller writes uses

            var typeLength = message[at++];
            int payloadLength;
            if ((header & 0x10) != 0) payloadLength = message[at++];
            else
            {
                if (at + 4 > message.Length) break;
                payloadLength = (message[at] << 24) | (message[at + 1] << 16) | (message[at + 2] << 8) | message[at + 3];
                at += 4;
            }
            var idLength = (header & 0x08) != 0 && at < message.Length ? message[at++] : 0;

            if (payloadLength < 0 || at + typeLength + idLength + payloadLength > message.Length) break;
            var type = message.Slice(at, typeLength).ToArray();
            at += typeLength + idLength;
            var payload = message.Slice(at, payloadLength).ToArray();
            at += payloadLength;

            records.Add(new NdefRecord((byte)(header & 0x07), type, payload));
            if ((header & 0x40) != 0) break; // ME
        }
        return records;
    }

    /// <summary>A message wrapped for a Type 2 tag's data area: NDEF TLV, then a terminator.</summary>
    public static byte[] WrapTlv(byte[] message)
    {
        byte[] length = message.Length < 0xFF
            ? [(byte)message.Length]
            : [0xFF, (byte)(message.Length >> 8), (byte)message.Length];
        return [TlvMessage, .. length, .. message, TlvTerminator];
    }

    /// <summary>
    /// Where the NDEF TLV sits in a data area and the message inside it. Lock and memory control
    /// TLVs some tags carry before it are stepped over, never rewritten. Without an NDEF TLV, the
    /// offset is where one should be written (the terminator's place, or the start) and the
    /// message is null.
    /// </summary>
    public static (int Offset, byte[]? Message) FindMessage(ReadOnlySpan<byte> area)
    {
        var at = 0;
        while (at < area.Length)
        {
            var tag = area[at];
            if (tag == TlvNull) { at++; continue; }
            if (tag == TlvTerminator) return (at, null);
            if (at + 2 > area.Length) break;

            var start = at;
            int length = area[at + 1];
            var value = at + 2;
            if (length == 0xFF)
            {
                if (at + 4 > area.Length) break;
                length = (area[at + 2] << 8) | area[at + 3];
                value = at + 4;
            }
            if (value + length > area.Length) break;

            if (tag == TlvMessage) return (start, area.Slice(value, length).ToArray());
            at = value + length;
        }
        return (0, null);
    }
}
