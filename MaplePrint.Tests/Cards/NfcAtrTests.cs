using MaplePrint.Cards;
using Xunit;

namespace MaplePrint.Tests.Cards;

public class NfcAtrTests
{
    [Fact]
    public void The_bench_card_reads_as_mifare_classic_1k()
        => Assert.Equal(NfcCardKind.MifareClassic1K, NfcAtr.KindOf(FakeType2Tag.Classic1KAtr));

    [Fact]
    public void An_ntag_reads_as_ultralight()
        => Assert.Equal(NfcCardKind.Ultralight, NfcAtr.KindOf(FakeType2Tag.UltralightAtr));

    [Fact]
    public void A_desfire_reads_as_iso_dep()
        => Assert.Equal(NfcCardKind.IsoDep, NfcAtr.KindOf([0x3B, 0x81, 0x80, 0x01, 0x80, 0x80]));

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x3B, 0x02, 0x14, 0x50 })]
    public void Anything_else_is_unknown(byte[] atr)
        => Assert.Equal(NfcCardKind.Unknown, NfcAtr.KindOf(atr));
}
