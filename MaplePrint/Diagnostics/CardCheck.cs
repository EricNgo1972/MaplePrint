using System.Runtime.Versioning;
using MaplePrint.Cards;
using MaplePrint.Settings;

namespace MaplePrint.Diagnostics;

public enum CardCheckKind { Step, Pass, Warn, Fail, Note }

public sealed record CardCheckLine(CardCheckKind Kind, string Text);

/// <summary>
/// The on-site check of the USB card reader — the hands-on half of the self-test, opened from the
/// tray's Test menu because it needs a person holding a card. It proves what a counter depends on,
/// in order: Windows sees the reader, the reader reads a card, the same card gives the same ID on a
/// second tap (that ID is the card's key in the shop's ledger), on an NTAG a link can be written
/// and read back, and the shop set in Settings answers. Whatever it writes it puts back, so a shop
/// can run it with cards it has already sold.
///
/// <para>Knows nothing about windows: it says lines and asks yes-or-no questions through the two
/// callbacks, and <see cref="CardCheckForm"/> shows them.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class CardCheck
{
    // Written during the check and then replaced with what the card held before.
    private const string TestLink = "https://maplekiosk.ca/nfc-test";

    // Long enough for someone reading the instructions off a phone call.
    private static readonly TimeSpan CardWait = TimeSpan.FromMinutes(2);

    /// <summary>Runs the check. True when everything it could check passed.</summary>
    public static async Task<bool> RunAsync(
        AppSettings settings, Action<CardCheckLine> say, Func<string, Task<bool>> askYes, CancellationToken ct)
    {
        void Step(string text) => say(new(CardCheckKind.Step, text));
        void Pass(string text) => say(new(CardCheckKind.Pass, text));
        void Warn(string text) => say(new(CardCheckKind.Warn, text));
        void Fail(string text, params string[] help)
        {
            say(new(CardCheckKind.Fail, text));
            foreach (var h in help) say(new(CardCheckKind.Note, h));
        }

        // ── Is it there? ──
        Step("Looking for card readers…");
        IReadOnlyList<string> readers;
        try { readers = NfcReader.ListReaders(); }
        catch (NfcException ex) { Fail(ex.Message); return false; }

        if (readers.Count == 0)
        {
            Fail("No card reader found.",
                "Plug it into a USB port and wait for Windows to say it is ready, then run this again.",
                "In Device Manager it shows under \"Smart card readers\".");
            return false;
        }
        foreach (var r in readers) Pass(r);

        var cardOk = await CardAsync(readers, Step, Pass, Warn, Fail, say, askYes, ct);
        var shopOk = await ShopAsync(settings, Step, Pass, Warn, Fail);
        return cardOk && shopOk;
    }

    private static async Task<bool> CardAsync(
        IReadOnlyList<string> readers, Action<string> Step, Action<string> Pass, Action<string> Warn,
        Action<string, string[]> Fail, Action<CardCheckLine> say, Func<string, Task<bool>> askYes, CancellationToken ct)
    {
        // The one the watcher uses too: the first ACS reader, else the first of any make.
        NfcReader reader;
        try { reader = NfcReader.Open(); }
        catch (NfcException ex) { Fail(ex.Message, []); return false; }

        using (reader)
        {
            if (readers.Count > 1) say(new(CardCheckKind.Note, $"Checking {reader.Name}."));
            try
            {
                // ── Does it read? ──
                Step("Lay a card flat on the reader.");
                string firstUid;
                NfcCardKind firstKind;
                using (var first = await reader.WaitForCardAsync(CardWait, ct))
                {
                    if (first is null)
                    {
                        Fail("No card was read in two minutes.", [
                            "Lay the card flat on the middle of the reader. The light turns green and it beeps when it sees one.",
                            "A 125 kHz card (most door fobs) can't be read by this reader."]);
                        return false;
                    }
                    Describe(first, Pass, say);
                    firstUid = first.Uid;
                    firstKind = first.Kind;
                }

                // ── Same card, same ID? ──
                Step("Lift the card off, then lay it back.");
                if (!await reader.WaitForRemovalAsync(CardWait, ct)) { Fail("The card stayed on the reader.", []); return false; }
                using var second = await reader.WaitForCardAsync(CardWait, ct);
                if (second is null) { Fail("The card did not come back.", []); return false; }

                if (second.Uid != firstUid)
                {
                    Fail($"The second tap gave a different ID ({second.Uid}).", [
                        firstKind == NfcCardKind.IsoDep
                            ? "Phones and bank cards change their ID on every tap, so they can't be a shop's cards."
                            : "Make sure it was the same card."]);
                    return false;
                }
                Pass($"Same ID both times: {firstUid}. This is the card's key.");

                // ── Does it write? ──
                if (!second.HasPages)
                {
                    Warn($"{NfcAtr.Describe(second.Kind)} is read by its ID only; there is nothing to write to.");
                    return true;
                }
                if (!second.IsWritable)
                {
                    Warn("This card is locked or not set up for NDEF, so the write test is skipped.");
                    return true;
                }
                if (!await askYes("Test writing? A test link is written, read back, then what the card held is put back."))
                {
                    Warn("Write test skipped.");
                    return true;
                }
                return await WriteTestAsync(reader, second, Step, Pass, Fail, ct);
            }
            catch (NfcException ex)
            {
                Fail(ex.Message, []);
                return false;
            }
        }
    }

    private static async Task<bool> WriteTestAsync(
        NfcReader reader, NfcCard card, Action<string> Step, Action<string> Pass, Action<string, string[]> Fail, CancellationToken ct)
    {
        var original = card.ReadDataArea();

        bool wrote;
        try
        {
            card.WriteNdef([NdefRecord.Uri(TestLink)]);
            var back = card.ReadNdef().Select(r => r.AsText()).FirstOrDefault();
            wrote = back == TestLink;
            if (wrote) Pass($"Wrote and read back {TestLink}");
            else Fail($"Read back \"{back}\" instead of the test link.", []);
        }
        catch (NfcException ex)
        {
            Fail(ex.Message, []);
            wrote = false;
        }

        // Put back what the card held. If the card left the reader mid-way, ask for it again — and
        // only for that card: writing one card's memory onto another would be worse than the test.
        try
        {
            card.WriteDataArea(original);
        }
        catch (NfcException)
        {
            Step($"Lay the same card back on the reader so it can be restored (ID {card.Uid}).");
            using var again = await reader.WaitForCardAsync(CardWait, ct);
            if (again is null || again.Uid != card.Uid)
            {
                Fail("The card was not restored. It still holds the test link.", ["Run this check again with that card to write over it."]);
                return false;
            }
            again.WriteDataArea(original);
        }
        Pass("Put back what the card held.");
        return wrote;
    }

    /// <summary>Last, because a reader that reads is worth knowing about even when the shop is down.</summary>
    private static async Task<bool> ShopAsync(
        AppSettings settings, Action<string> Step, Action<string> Pass, Action<string> Warn, Action<string, string[]> Fail)
    {
        Step("Reaching the shop…");
        if (!settings.HasCardShop)
        {
            Warn("No shop is set, so cards read here go nowhere. Set the shop's address in Settings → Card reader.");
            return false;
        }

        try
        {
            using var client = new ShopClient();
            var hello = await client.HelloAsync(settings.CardShopUrl, settings.CardDeviceKey, CancellationToken.None);
            if (hello.Paired)
            {
                Pass($"Paired with {settings.CardShopUrl} as \"{hello.ReaderName}\". Cards go to the counters it is attached to.");
                return true;
            }
            Warn($"Reached {settings.CardShopUrl}, but this reader isn't paired yet. On the shop's card readers setup page, "
               + $"add a \"USB card reader on a counter PC\" and enter the code {hello.PairCode}.");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Fail($"Could not reach the shop at {settings.CardShopUrl}: {ShopClient.Reason(ex)}.",
                ["Check the address in Settings, and that this PC is online."]);
            return false;
        }
    }

    private static void Describe(NfcCard card, Action<string> Pass, Action<CardCheckLine> say)
    {
        Pass($"Read {NfcAtr.Describe(card.Kind)}");
        say(new(CardCheckKind.Note, $"ID: {card.Uid}"));
        if (!card.HasPages) return;

        say(new(CardCheckKind.Note, card.Capacity > 0
            ? $"Room for {card.Capacity} bytes{(card.IsWritable ? "" : ", read-only")}."
            : "Not set up for NDEF."));
        foreach (var text in card.ReadNdef().Select(r => r.AsText()).Where(t => t is not null))
            say(new(CardCheckKind.Note, $"Holds: {text}"));
    }
}
