using MaplePrint.Cards;
using MaplePrint.Settings;
using Xunit;

namespace MaplePrint.Tests.Cards;

public class CardWatcherTests
{
    [Fact]
    public void A_settings_file_from_before_card_readers_sends_cards_nowhere()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "ListenPort": 9110 }""")!;

        Assert.False(settings.HasCardShop);
    }

    [Theory]
    [InlineData("37B93304", "37…04")]
    [InlineData("04A1B2C3D4E5F6", "04…F6")]
    [InlineData("ABCD", "ABCD")]
    public void The_log_shows_enough_of_an_id_to_tell_cards_apart(string uid, string shown)
        => Assert.Equal(shown, CardWatcher.Masked(uid));

    [Fact]
    public void A_counter_with_no_reader_and_no_shop_is_not_asked_for_one()
        => Assert.Equal("Card reader: none plugged in",
            CardWatcher.StatusFor(null, CardWatcher.Shop.NotSet, null, null, paused: false));

    [Fact]
    public void The_pairing_code_is_shown_before_the_reader_is_plugged_in()
        => Assert.Contains("482913", CardWatcher.StatusFor(null, CardWatcher.Shop.NotPaired, "482913", null, paused: false));
}
