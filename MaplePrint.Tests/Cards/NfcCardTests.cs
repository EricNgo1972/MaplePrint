using MaplePrint.Cards;
using Xunit;

namespace MaplePrint.Tests.Cards;

public class NfcCardTests
{
    [Fact]
    public void The_uid_is_uppercase_hex_with_no_separators()
    {
        using var card = FakeType2Tag.Ntag213().Connect();

        Assert.Equal("04A1B2C3D4E5F6", card.Uid);
    }

    [Fact]
    public void An_ntag213_declares_144_bytes_and_is_writable()
    {
        using var card = FakeType2Tag.Ntag213().Connect();

        Assert.True(card.HasPages);
        Assert.Equal(144, card.Capacity);
        Assert.True(card.IsWritable);
    }

    [Fact]
    public void A_fresh_card_holds_no_records()
    {
        using var card = FakeType2Tag.Ntag213().Connect();

        Assert.Empty(card.ReadNdef());
    }

    [Fact]
    public void A_link_written_reads_back()
    {
        var tag = FakeType2Tag.Ntag213();
        using (var card = tag.Connect()) card.WriteNdef([NdefRecord.Uri("https://salon.example/gift-card/balance?code=34HW-ZKX9-J5WA")]);

        using var again = tag.Connect();
        Assert.Equal("https://salon.example/gift-card/balance?code=34HW-ZKX9-J5WA", Assert.Single(again.ReadNdef()).AsText());
    }

    [Fact]
    public void Nothing_is_ever_written_outside_the_data_area()
    {
        var tag = FakeType2Tag.Ntag213();
        using var card = tag.Connect();

        card.WriteNdef([NdefRecord.Uri("https://example.com/" + new string('a', 100))]);
        card.WriteDataArea(new byte[144]);

        Assert.NotEmpty(tag.Writes);
        Assert.All(tag.Writes, page => Assert.InRange(page, 4, 39));
    }

    [Fact]
    public void Only_the_pages_that_change_are_written()
    {
        var tag = FakeType2Tag.Ntag213();
        using var card = tag.Connect();
        card.WriteNdef([NdefRecord.Text("same")]);
        tag.Writes.Clear();

        card.WriteNdef([NdefRecord.Text("same")]);

        Assert.Empty(tag.Writes);
    }

    [Fact]
    public void A_message_too_big_for_the_card_is_refused_before_anything_is_written()
    {
        var tag = FakeType2Tag.Ntag213();
        using var card = tag.Connect();

        var ex = Assert.Throws<NfcException>(() => card.WriteNdef([NdefRecord.Uri("https://example.com/" + new string('a', 200))]));

        Assert.Contains("holds 144", ex.Message);
        Assert.Empty(tag.Writes);
    }

    [Fact]
    public void A_read_only_card_is_refused()
    {
        var tag = FakeType2Tag.Ntag213(access: 0x0F);
        using var card = tag.Connect();

        Assert.False(card.IsWritable);
        Assert.Throws<NfcException>(() => card.WriteNdef([NdefRecord.Text("x")]));
        Assert.Empty(tag.Writes);
    }

    [Fact]
    public void A_card_never_formatted_is_refused_rather_than_formatted()
    {
        // Formatting writes the capability container, which can only ever be written once.
        var tag = FakeType2Tag.Ntag213(magic: 0x00);
        using var card = tag.Connect();

        Assert.Equal(0, card.Capacity);
        Assert.Empty(card.ReadNdef());
        Assert.Throws<NfcException>(() => card.WriteNdef([NdefRecord.Text("x")]));
        Assert.Empty(tag.Writes);
    }

    [Fact]
    public void A_mifare_classic_is_read_by_its_id_only()
    {
        var tag = FakeType2Tag.Ntag213();
        using var card = tag.Connect(FakeType2Tag.Classic1KAtr);

        Assert.Equal(NfcCardKind.MifareClassic1K, card.Kind);
        Assert.False(card.HasPages);
        Assert.Empty(card.ReadNdef());
        var ex = Assert.Throws<NfcException>(() => card.WriteNdef([NdefRecord.Text("x")]));
        Assert.Contains("read by its ID only", ex.Message);
        Assert.Empty(tag.Writes);
    }

    [Fact]
    public void Restoring_the_area_read_before_puts_the_card_back_exactly()
    {
        var tag = FakeType2Tag.Ntag213();
        tag.SetData(0, Ndef.WrapTlv(Ndef.Encode([NdefRecord.Uri("https://shop.example/card/1")])));
        using var card = tag.Connect();
        var before = card.ReadDataArea();

        card.WriteNdef([NdefRecord.Uri("https://maplekiosk.ca/nfc-test")]);
        card.WriteDataArea(before);

        Assert.Equal(before, card.ReadDataArea());
        Assert.Equal("https://shop.example/card/1", Assert.Single(card.ReadNdef()).AsText());
    }

    [Fact]
    public void A_card_lifted_off_mid_write_says_so()
    {
        var tag = FakeType2Tag.Ntag213();
        using var card = tag.Connect();
        tag.RemovedAfterWrites = 1;

        var ex = Assert.Throws<NfcException>(() => card.WriteNdef([NdefRecord.Uri("https://example.com/" + new string('a', 40))]));

        Assert.Contains("lifted off", ex.Message);
    }

    [Fact]
    public void A_refusal_from_the_card_carries_its_status_word()
    {
        var tag = FakeType2Tag.Ntag213(sizeUnits: 0xFF); // claims more pages than it has
        using var card = tag.Connect();

        var ex = Assert.Throws<NfcException>(() => card.WriteDataArea(new byte[200]));

        Assert.Equal(0x6300, ex.Code);
    }
}
