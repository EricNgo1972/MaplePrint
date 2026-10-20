using MaplePrint.Cards;
using Xunit;

namespace MaplePrint.Tests.Cards;

public class NdefTests
{
    [Fact]
    public void A_link_is_stored_with_its_prefix_as_one_byte()
    {
        // The NFC Forum's own example: "https://www." is prefix 0x02.
        var message = Ndef.Encode([NdefRecord.Uri("https://www.google.com")]);

        byte[] expected = [0xD1, 0x01, 0x0B, 0x55, 0x02, .. "google.com"u8.ToArray()];
        Assert.Equal(expected, message);
    }

    [Theory]
    [InlineData("https://salon.example/wallet/gift-card/34HW-ZKX9-J5WA.pkpass")]
    [InlineData("http://example.com/x")]
    [InlineData("tel:+15145550100")]
    [InlineData("custom-scheme:abc")]
    public void A_link_reads_back_in_full(string link)
    {
        var records = Ndef.Decode(Ndef.Encode([NdefRecord.Uri(link)]));

        Assert.Equal(link, Assert.Single(records).AsText());
    }

    [Fact]
    public void A_text_reads_back_as_its_text_without_the_language()
    {
        var records = Ndef.Decode(Ndef.Encode([NdefRecord.Text("Thẻ quà tặng 34HW-ZKX9-J5WA", "vi")]));

        Assert.Equal("Thẻ quà tặng 34HW-ZKX9-J5WA", Assert.Single(records).AsText());
    }

    [Fact]
    public void Several_records_keep_their_order()
    {
        var records = Ndef.Decode(Ndef.Encode([NdefRecord.Text("first"), NdefRecord.Uri("https://second.example")]));

        Assert.Equal(new[] { "first", "https://second.example" }, records.Select(r => r.AsText()));
    }

    [Fact]
    public void A_payload_of_256_bytes_or_more_uses_the_long_form()
    {
        var link = "https://example.com/" + new string('a', 300);
        var message = Ndef.Encode([NdefRecord.Uri(link)]);

        Assert.Equal(0, message[0] & 0x10); // SR clear
        Assert.Equal(link, Assert.Single(Ndef.Decode(message)).AsText());
    }

    [Fact]
    public void A_message_cut_short_returns_the_records_before_the_cut()
    {
        var message = Ndef.Encode([NdefRecord.Text("kept"), NdefRecord.Text("cut off")]);

        var records = Ndef.Decode(message.AsSpan(0, message.Length - 3));

        Assert.Equal("kept", Assert.Single(records).AsText());
    }

    [Fact]
    public void Records_the_phone_ignores_read_as_null_not_as_garbage()
    {
        var mime = new NdefRecord(0x02, "text/vcard"u8.ToArray(), "BEGIN:VCARD"u8.ToArray());

        Assert.Null(Assert.Single(Ndef.Decode(Ndef.Encode([mime]))).AsText());
    }

    [Fact]
    public void The_message_is_found_past_null_and_lock_control_TLVs()
    {
        var message = Ndef.Encode([NdefRecord.Text("x")]);
        byte[] area = [0x00, 0x01, 0x03, 0xA0, 0x0C, 0x34, .. Ndef.WrapTlv(message), 0x00, 0x00];

        var (offset, found) = Ndef.FindMessage(area);

        Assert.Equal(6, offset);
        Assert.Equal(message, found);
    }

    [Fact]
    public void A_message_of_255_bytes_or_more_uses_the_three_byte_length()
    {
        var message = new byte[300];
        var tlv = Ndef.WrapTlv(message);

        Assert.Equal(new byte[] { 0x03, 0xFF, 0x01, 0x2C }, tlv[..4]);
        Assert.Equal(message, Ndef.FindMessage(tlv).Message);
    }

    [Fact]
    public void An_area_with_only_a_terminator_has_no_message_and_writes_at_the_terminator()
    {
        var (offset, message) = Ndef.FindMessage([0x00, 0xFE, 0x00, 0x00]);

        Assert.Equal(1, offset);
        Assert.Null(message);
    }
}
