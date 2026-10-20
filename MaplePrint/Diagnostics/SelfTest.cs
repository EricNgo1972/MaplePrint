using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using MaplePrint.Cards;
using MaplePrint.Printing;
using MaplePrint.Server;
using MaplePrint.Settings;

namespace MaplePrint.Diagnostics;

/// <summary>
/// The checks that need no printer, no drawer and no counter — run headless with
/// <c>MaplePrint.exe --self-test</c>, results in
/// <c>%TEMP%\mapleprint-selftest.txt</c> and the outcome in the exit code.
///
/// <para>Here rather than in a test project because MaplePrint ships as one
/// self-contained .exe with no external packages, and a test runner would be the
/// first. The trade is real — no discovery, no isolation, one bad assertion takes
/// out the run — and it buys something back: these run on the counter PC that is
/// actually misbehaving, not only on a developer's machine.</para>
///
/// <para>What is deliberately not here: anything needing hardware. Whether a
/// TSP143 actually opens its drawer is a question paper and a solenoid answer,
/// and the README carries that checklist. The card reader's hands-on half — a
/// person laying a card on it — is <see cref="CardCheck"/>, on the tray's menu;
/// what this checks of it is everything up to the reader: the settings, the
/// status line, and the two calls to the shop.</para>
/// </summary>
public static class SelfTest
{
    private static readonly List<string> Lines = [];
    private static int _failed;
    private static int _passed;

    public static int Run(string? resultPath = null)
    {
        resultPath ??= Path.Combine(Path.GetTempPath(), "mapleprint-selftest.txt");

        Lines.Add($"MaplePrint self-test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Lines.Add("");

        try
        {
            DrawerBytes();
            DrawerPlanning();
            Configuration();
            RetrySuppression();
            Serialization().GetAwaiter().GetResult();
            Compatibility();
            SettingsRoundTrip();
            OldClientOverHttp().GetAwaiter().GetResult();
            CardSettings();
            CardStatus();
            CardsToTheShop().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Fail("harness", $"{ex.GetType().Name}: {ex.Message}");
        }

        Lines.Add("");
        Lines.Add($"result: {_passed} passed, {_failed} failed");

        try { File.WriteAllLines(resultPath, Lines); } catch { }

        return _failed == 0 ? 0 : 1;
    }

    // ---- the bytes ------------------------------------------------------

    private static void DrawerBytes()
    {
        Section("drawer bytes");

        var star = PrinterCommandSets.Star;
        var esc = PrinterCommandSets.EscPos;

        // The regression that matters. The time unit differs — 10ms on Star,
        // 2ms on ESC/POS — so one configured pulse must become two different
        // bytes. Getting this wrong gives a 40ms pulse that clicks the solenoid
        // without throwing the latch, and presents as "it randomly doesn't open".
        Hex("star 200ms", star.KickDrawer(200), "1B071414" + "07");
        Hex("escpos 200ms", esc.KickDrawer(200), "1B7000" + "6464");

        Hex("star 250ms", star.KickDrawer(250), "1B071919" + "07");
        Hex("star 300ms", star.KickDrawer(300), "1B071E1E" + "07");

        Hex("escpos 250ms", esc.KickDrawer(250), "1B7000" + "7D7D");
        Hex("escpos 300ms", esc.KickDrawer(300), "1B7000" + "9696");

        // Never a bare drive byte: the pulse-width command is sticky on some
        // firmware, so a lone kick inherits whatever the last job set.
        Check("star frame is 5 bytes", star.KickDrawer(200).Length == 5);
        Check("escpos frame is 5 bytes", esc.KickDrawer(200).Length == 5);

        Hex("star unit 2 drives SUB", star.KickDrawer(200, 2), "1B0714141A");
        Hex("escpos unit 2 sets m=01", esc.KickDrawer(200, 2), "1B7001" + "6464");

        // Clamped, not cast. A cast wraps, and a wrapped pulse is a wildly wrong
        // one — 1300ms would become 2ms on a Star.
        Hex("star clamps at 0x7F", star.KickDrawer(5000), "1B077F7F07");
        Hex("escpos clamps at 0xFF", esc.KickDrawer(5000), "1B7000FFFF");

        Hex("star floors at 0x01", star.KickDrawer(1), "1B07010107");
        Hex("escpos floors at 0x01", esc.KickDrawer(1), "1B70000101");

        Check("star ceiling is 1270ms", star.MaxPulseMs == 1270);
        Check("escpos ceiling is 510ms", esc.MaxPulseMs == 510);

        Check("star guessed from a TSP queue name",
            PrinterCommandSets.GuessFor("Star TSP100 Cutter (TSP143)") == DrawerCommands.Star);
        Check("escpos guessed for everything else",
            PrinterCommandSets.GuessFor("EPSON TM-T88VI Receipt") == DrawerCommands.EscPos);
    }

    // ---- the three scenarios --------------------------------------------

    private static void DrawerPlanning()
    {
        Section("the three scenarios");

        var payload = new byte[] { 0x41, 0x42, 0x43 };

        var raw = Destination("raw-receipt", DestinationKind.Raw, DrawerCommands.EscPos);
        var doc = Destination("doc-receipt", DestinationKind.Document, DrawerCommands.Star);

        // 1 — print and open, on a raw destination: one job, kick first.
        var one = DrawerDispatch.Plan(raw, PrinterCommandSets.EscPos, ContentTypes.Raw, payload, openDrawer: true);
        Check("raw print+open is one send", one.Count == 1);
        Hex("raw print+open puts the kick first", one[0].Bytes, "1B7000646441" + "4243");

        // 1 — the same on a document destination, which cannot be prepended to:
        // the receipt is a rendered page, so the kick goes ahead of it as its own
        // RAW job on the same queue.
        var onDoc = DrawerDispatch.Plan(doc, PrinterCommandSets.Star, ContentTypes.Image, payload, openDrawer: true);
        Check("document print+open is two sends", onDoc.Count == 2);
        Check("document kick goes first", onDoc.Count == 2 && onDoc[0].Kind == SendKind.Raw && onDoc[0].ContainsKick);
        Check("document page follows untouched",
            onDoc.Count == 2 && onDoc[1].Kind == SendKind.Image && onDoc[1].Bytes.SequenceEqual(payload));

        // 2 — print, drawer stays shut. The bytes are the payload and nothing else.
        var two = DrawerDispatch.Plan(raw, null, ContentTypes.Raw, payload, openDrawer: false);
        Check("print-only is one send", two.Count == 1);
        Check("print-only sends the payload untouched", two.Count == 1 && two[0].Bytes.SequenceEqual(payload));
        Check("print-only carries no kick", two.Count == 1 && !two[0].ContainsKick);

        // 3 — no sale. The kick alone; no paper moves.
        var three = DrawerDispatch.Plan(doc, PrinterCommandSets.Star, ContentTypes.Image, [], openDrawer: true);
        Check("no-sale is one send", three.Count == 1);
        Hex("no-sale is the kick alone", three[0].Bytes, "1B071414" + "07");

        // The job that asks for nothing. Nothing is sent.
        var none = DrawerDispatch.Plan(doc, null, ContentTypes.Image, [], openDrawer: false);
        Check("empty payload with no drawer sends nothing", none.Count == 0);

        // A suppressed retry of a no-sale: no command set, no payload, no sends.
        var suppressed = DrawerDispatch.Plan(doc, null, ContentTypes.Image, [], openDrawer: true);
        Check("suppressed no-sale sends nothing", suppressed.Count == 0);
    }

    // ---- configuration ---------------------------------------------------

    private static void Configuration()
    {
        Section("configuration");

        Check("no drawer command means no drawer",
            !Destination("d", DestinationKind.Document, null).HasDrawer);

        Check("blank drawer command means no drawer",
            !Destination("d", DestinationKind.Document, "   ").HasDrawer);

        var unknown = Destination("d", DestinationKind.Document, "epson");
        Check("an unrecognised command set is refused", DrawerDispatch.Problem(unknown) is not null);

        // The two axes interact: 600ms is legal on a Star and unreachable on an
        // ESC/POS printer, whose pulse byte tops out at 510ms. Clamping it
        // quietly would give the drawer a pulse the settings do not say.
        var longStar = Destination("d", DestinationKind.Document, DrawerCommands.Star, pulseMs: 600);
        var longEsc = Destination("d", DestinationKind.Document, DrawerCommands.EscPos, pulseMs: 600);
        Check("600ms is fine on a Star", DrawerDispatch.Problem(longStar) is null);
        Check("600ms is refused on ESC/POS", DrawerDispatch.Problem(longEsc) is not null);

        var badUnit = Destination("d", DestinationKind.Document, DrawerCommands.Star);
        badUnit.DrawerUnit = 3;
        Check("peripheral unit 3 is refused", DrawerDispatch.Problem(badUnit) is not null);

        var ok = Destination("d", DestinationKind.Document, DrawerCommands.Star);
        Check("a plain drawer is accepted", DrawerDispatch.Problem(ok) is null);
        Check("pulse defaults to 200ms", ok.EffectiveDrawerPulseMs == 200);
        Check("unit defaults to 1", ok.EffectiveDrawerUnit == 1);
    }

    // ---- retry -----------------------------------------------------------

    private static void RetrySuppression()
    {
        Section("retry suppression");

        var ledger = new DrawerKickLedger();

        Check("the first attempt may kick", ledger.TryClaim("order-1042"));
        Check("the retry may not", !ledger.TryClaim("order-1042"));
        Check("a different job still may", ledger.TryClaim("order-1043"));

        // A job id we minted is unique per request, so it can never match a
        // retry — there is nothing to claim and nothing to suppress.
        Check("no job id, nothing to suppress", ledger.TryClaim(null));
        Check("no job id, still nothing to suppress", ledger.TryClaim(""));

        // The window is what stops a genuine second sale looking like a retry.
        var now = DateTime.UtcNow;
        var clock = now;
        var windowed = new DrawerKickLedger(TimeSpan.FromMinutes(10), () => clock);

        Check("claimed inside the window", windowed.TryClaim("order-9"));
        clock = now.AddMinutes(5);
        Check("still suppressed five minutes later", !windowed.TryClaim("order-9"));
        clock = now.AddMinutes(11);
        Check("free again once the window passes", windowed.TryClaim("order-9"));
    }

    // ---- per-device serialization ---------------------------------------

    private static async Task Serialization()
    {
        Section("per-device serialization");

        var active = new ConcurrentDictionary<string, int>();
        var worst = new ConcurrentDictionary<string, int>();
        var order = new ConcurrentQueue<string>();
        var gate = new object();

        await using var dispatcher = new PrintDispatcher(async (job, ct) =>
        {
            lock (gate)
            {
                var now = active.AddOrUpdate(job.DeviceKey, 1, (_, n) => n + 1);
                worst.AddOrUpdate(job.DeviceKey, now, (_, n) => Math.Max(n, now));
            }

            await Task.Delay(15, ct);

            lock (gate) active.AddOrUpdate(job.DeviceKey, 0, (_, n) => n - 1);

            order.Enqueue(job.JobId);
            return null;
        });

        // Eight at once on one printer, and four on a second. Two jobs
        // interleaved on one printer produce half of each receipt — and with a
        // drawer attached, a mangled job may still have fired the till.
        var first = Enumerable.Range(0, 8).Select(i => dispatcher.DispatchAsync(Job("queue:a", $"a{i}")));
        var second = Enumerable.Range(0, 4).Select(i => dispatcher.DispatchAsync(Job("queue:b", $"b{i}")));

        var results = await Task.WhenAll(first.Concat(second));

        Check("every job completed", results.All(r => r.Outcome == DispatchOutcome.Done && r.Error is null));
        Check("printer A ran one job at a time", worst.GetValueOrDefault("queue:a") == 1);
        Check("printer B ran one job at a time", worst.GetValueOrDefault("queue:b") == 1);
        Check("both printers were used", dispatcher.DeviceCount == 2);
        Check("all twelve jobs ran", order.Count == 12);

        // One queue, one consumer, so a printer's own jobs keep their order.
        var seen = order.Where(id => id.StartsWith('a')).ToArray();
        Check("printer A kept its order", seen.SequenceEqual(Enumerable.Range(0, 8).Select(i => $"a{i}")));
    }

    // ---- the wire, as callers already speak it --------------------------

    /// <summary>
    /// The shipped MapleCoffee client sends six fields and reads five back, and
    /// is not being updated in step with this. Nothing added here may change what
    /// it sees.
    /// </summary>
    private static void Compatibility()
    {
        Section("backward compatibility");

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Exactly what MaplePrintStation.PrintRequestDto puts on the wire.
        const string old = """
            {"destination":"receipt","contentType":"image","payloadBase64":"QUJD",
             "description":"Order #1042","jobId":null,"copies":1}
            """;

        var parsed = JsonSerializer.Deserialize<PrintRequest>(old, web);
        Check("a pre-drawer request still binds", parsed?.DestinationName == "receipt");
        Check("a pre-drawer request never opens the drawer", parsed?.OpenDrawer == false);
        Check("a pre-drawer request names no user", parsed?.User is null);

        // The old client deserialises into a five-field DTO. Unknown members are
        // ignored by System.Text.Json, so additions are safe — but a field that
        // is null must not be written at all where it would read as a value.
        var ok = JsonSerializer.Serialize(new PrintResponse("j1", true, "receipt", "Star TSP100"), web);
        Check("a plain response carries drawerOpened", ok.Contains("\"drawerOpened\":false"));
        Check("a plain response omits retryable", !ok.Contains("retryable"));

        var noSale = JsonSerializer.Serialize(
            new PrintResponse("j2", false, "receipt", "Star TSP100", "offline", Retryable: false), web);
        Check("a no-sale failure says do not retry", noSale.Contains("\"retryable\":false"));

        // A destination with no drawer must look exactly as it did before.
        var plain = JsonSerializer.Serialize(
            new DestinationInfo("label", "raw", "raw", "zpl", "DRINK LABEL", 448, 203, true, true), web);
        Check("a destination with no drawer says nothing about one", !plain.Contains("drawer"));

        var withDrawer = JsonSerializer.Serialize(
            new DestinationInfo("receipt", "document", "image", null, "Star TSP100", 576, 203, true, true,
                new DrawerInfo(DrawerCommands.Star, 200, 1)), web);
        Check("a destination with a drawer advertises it", withDrawer.Contains("\"commandSet\":\"star\""));
    }

    // ---- settings.json --------------------------------------------------

    /// <summary>
    /// An existing install must load, save, and come back unchanged. The drawer
    /// keys are absent from every file written before this existed, and a
    /// destination without one must not start writing them.
    /// </summary>
    private static void SettingsRoundTrip()
    {
        Section("settings.json");

        // Mirrors JsonSettingsStore's own options, whose converter is why kinds
        // read as "Document" rather than 0 in a file somebody opens mid-support-call.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        // Exactly what an installed settings.json holds today.
        const string existing = """
            {"name":"receipt","queue":"Star TSP100 Cutter (TSP143)","kind":"Document",
             "dotWidth":576,"renderDpi":203,"enabled":true}
            """;

        var loaded = JsonSerializer.Deserialize<Destination>(existing, web)!;
        Check("an existing destination loads", loaded.Queue == "Star TSP100 Cutter (TSP143)");
        Check("an existing destination has no drawer", !loaded.HasDrawer);

        var written = JsonSerializer.Serialize(loaded, web);
        Check("and saves without inventing one", !written.Contains("drawer", StringComparison.OrdinalIgnoreCase));

        // The file's own history: get-only properties were being written into
        // settings.json as if they were settings, and read back as noise.
        Check("computed drawer values are not written",
            !written.Contains("hasDrawer") && !written.Contains("effective", StringComparison.OrdinalIgnoreCase));

        loaded.DrawerCommand = DrawerCommands.Star;
        loaded.DrawerPulseMs = 300;
        var withDrawer = loaded;
        var back = JsonSerializer.Deserialize<Destination>(JsonSerializer.Serialize(withDrawer, web), web)!;
        Check("a drawer survives a round trip",
            back.DrawerCommand == DrawerCommands.Star && back.EffectiveDrawerPulseMs == 300 && back.EffectiveDrawerUnit == 1);
    }

    // ---- the old client, over real HTTP ---------------------------------

    /// <summary>
    /// The whole path, against a real listener: the shipped client's exact JSON
    /// in, its exact five-field shape and its exact status codes out. Binding a
    /// record through the minimal API is not the same as deserialising one, and
    /// this is the part a shop notices if it is wrong.
    /// </summary>
    private static async Task OldClientOverHttp()
    {
        Section("the old client, over HTTP");

        var folder = Path.Combine(Path.GetTempPath(), "mapleprint-selftest");
        Directory.CreateDirectory(folder);

        var settings = new AppSettings
        {
            StationName = "SELFTEST",
            AllowRemote = false,
            Destinations =
            [
                // No queue assigned: reaches every check except the printer
                // itself, which is the furthest this can go without hardware.
                new Destination { Name = "receipt", Kind = DestinationKind.Document, Queue = "" },
                new Destination { Name = "label", Kind = DestinationKind.Raw, Language = PrinterLanguages.Zpl, Queue = "" }
            ]
        };

        PrintServer? server = null;

        foreach (var port in new[] { 19110, 19111, 19112 })
        {
            settings.ListenPort = port;
            var candidate = new PrintServer(settings, new ActivityLog(folder));
            await candidate.StartAsync();

            if (candidate.State == ServerState.Listening) { server = candidate; break; }
            await candidate.DisposeAsync();
        }

        if (server is null)
        {
            Fail("start a listener", "ports 19110-19112 are all in use");
            return;
        }

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings.ListenPort}") };

            var health = await http.GetAsync("/health");
            var healthBody = await health.Content.ReadAsStringAsync();
            Check("GET /health is 200", health.StatusCode == HttpStatusCode.OK);
            Check("GET /health still has the four old fields",
                healthBody.Contains("\"status\"") && healthBody.Contains("\"station\"") &&
                healthBody.Contains("\"version\"") && healthBody.Contains("\"authRequired\""));

            var dests = await http.GetAsync("/destinations");
            var destsBody = await dests.Content.ReadAsStringAsync();
            Check("GET /destinations is 200", dests.StatusCode == HttpStatusCode.OK);
            Check("a destination with no drawer says nothing about one over the wire",
                !destsBody.Contains("drawer", StringComparison.OrdinalIgnoreCase));
            Check("GET /printers is still an alias",
                (await http.GetAsync("/printers")).StatusCode == HttpStatusCode.OK);

            // Byte for byte what MaplePrintStation.PrintRequestDto serialises.
            var (status, body) = await Post(http, "/print", """
                {"destination":"receipt","contentType":"image","payloadBase64":"QUJD",
                 "description":"Order #1042","jobId":null,"copies":1}
                """);

            // 409, not 404: the destination exists and has no printer assigned.
            Check("an old-style job reaches the station-state checks", status == HttpStatusCode.Conflict);
            Check("and is answered in the five fields the old client reads",
                body.Contains("\"jobId\"") && body.Contains("\"success\":false") &&
                body.Contains("\"destination\":\"receipt\"") && body.Contains("\"error\""));

            var (unknown, _) = await Post(http, "/print", """
                {"destination":"nope","contentType":"image","payloadBase64":"QUJD","copies":1}
                """);
            Check("an unknown destination is still 404", unknown == HttpStatusCode.NotFound);

            var (wrongType, _) = await Post(http, "/print", """
                {"destination":"label","contentType":"image","payloadBase64":"QUJD","copies":1}
                """);
            Check("the wrong content type is still 415", wrongType == HttpStatusCode.UnsupportedMediaType);

            var (noPayload, noPayloadBody) = await Post(http, "/print", """
                {"destination":"receipt","contentType":"image","payloadBase64":"","copies":1}
                """);
            Check("a missing payload is still 400", noPayload == HttpStatusCode.BadRequest);
            Check("with the message it always had", noPayloadBody.Contains("payloadBase64 is required"));

            var (badCopies, _) = await Post(http, "/print", """
                {"destination":"receipt","contentType":"image","payloadBase64":"QUJD","copies":0}
                """);
            Check("bad copies is still 400", badCopies == HttpStatusCode.BadRequest);

            // And the new field, on a printer that has no drawer: configuration,
            // answered with a code the old client already understands.
            var (noDrawer, _) = await Post(http, "/print", """
                {"destination":"receipt","contentType":"image","payloadBase64":"QUJD","openDrawer":true}
                """);
            Check("asking a printer with no drawer to open one is 409", noDrawer == HttpStatusCode.Conflict);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ---- the card reader ---------------------------------------------------

    private static void CardSettings()
    {
        Section("card reader settings");

        Check("a bare address gets https", AppSettings.NormalizeShopUrl("cafe.example.com") == "https://cafe.example.com");
        Check("a trailing slash goes", AppSettings.NormalizeShopUrl("https://cafe.example.com/") == "https://cafe.example.com");
        Check("http is kept for a LAN shop", AppSettings.NormalizeShopUrl("http://192.168.1.5:5000") == "http://192.168.1.5:5000");
        Check("a query is dropped", AppSettings.NormalizeShopUrl("cafe.example.com/?x=1") == "https://cafe.example.com");
        Check("blank is no shop", AppSettings.NormalizeShopUrl("  ") is null);
        Check("an ftp address is refused", AppSettings.NormalizeShopUrl("ftp://cafe.example.com") is null);

        // Every settings file written before the card reader: no shop, so cards go nowhere.
        var old = JsonSerializer.Deserialize<AppSettings>("""{"listenPort":9110}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Check("an existing install has no card shop", !old.HasCardShop);
        Check("a shop with no key is no shop", !new AppSettings { CardShopUrl = "https://cafe.example.com" }.HasCardShop);

        var a = AppSettings.NewCardDeviceKey();
        Check("a reader's key is long enough for the shop (32+)", a.Length >= 32);
        Check("a reader's key is URL-safe", a.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Check("two readers never share a key", a != AppSettings.NewCardDeviceKey());

        Check("the log shows enough of an ID to tell cards apart", CardWatcher.Masked("37B93304") == "37…04");
        Check("a short ID is shown whole", CardWatcher.Masked("ABCD") == "ABCD");
    }

    private static void CardStatus()
    {
        Section("card reader status");

        static string S(string? reader, CardWatcher.Shop shop, bool paused = false)
            => CardWatcher.StatusFor(reader, shop, "482913", "Front counter", paused);

        // Most counters have no card reader; they must not be nagged about a shop.
        Check("no reader and no shop says only that", S(null, CardWatcher.Shop.NotSet) == "Card reader: none plugged in");
        Check("a reader with no shop asks for one", S("ACR122U", CardWatcher.Shop.NotSet).Contains("no shop set"));
        Check("the pairing code is in the menu", S("ACR122U", CardWatcher.Shop.NotPaired).Contains("482913"));
        Check("the code shows even before the reader is plugged in", S(null, CardWatcher.Shop.NotPaired).Contains("482913"));
        Check("paired says as what", S("ACR122U", CardWatcher.Shop.Paired).Contains("\"Front counter\""));
        Check("paired with the reader unplugged says it is unplugged", S(null, CardWatcher.Shop.Paired) == "Card reader: none plugged in");
        Check("an unreachable shop says so", S("ACR122U", CardWatcher.Shop.Unreachable).Contains("can't reach"));
        Check("the check says cards are not sent", S("ACR122U", CardWatcher.Shop.Paired, paused: true).Contains("not sent"));
    }

    /// <summary>
    /// The two calls, against a stand-in for the shop's <c>/card-reader/api/</c> answering as
    /// <c>PhoneCardReaderEndpoints</c> does: camelCase, 404 for a reader that isn't paired.
    /// </summary>
    private static async Task CardsToTheShop()
    {
        Section("card reader, to the shop over HTTP");

        const string pairedKey = "paired-key-0123456789abcdefghijklmnop";
        const string strangerKey = "unpaired-key-0123456789abcdefghijklmn";
        var posted = new ConcurrentQueue<JsonElement>();

        WebApplication? shop = null;
        var port = 0;
        foreach (var candidate in new[] { 19120, 19121, 19122 })
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, candidate));
            var app = builder.Build();

            app.MapPost("/card-reader/api/hello", async (HttpContext ctx) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonElement>();
                return body.GetProperty("key").GetString() == pairedKey
                    ? Results.Json(new { paired = true, pairCode = (string?)null, readerName = "Front counter" })
                    : Results.Json(new { paired = false, pairCode = "482913", readerName = (string?)null });
            });
            app.MapPost("/card-reader/api/read", async (HttpContext ctx) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonElement>();
                if (body.GetProperty("key").GetString() != pairedKey) return Results.NotFound();
                posted.Enqueue(body.Clone());
                return Results.Json(new { heard = 2 });
            });

            try { await app.StartAsync(); shop = app; port = candidate; break; }
            catch { await app.DisposeAsync(); }
        }

        if (shop is null)
        {
            Fail("start a stand-in shop", "ports 19120-19122 are all in use");
            return;
        }

        try
        {
            var url = $"http://127.0.0.1:{port}";
            using var client = new ShopClient();

            var paired = await client.HelloAsync(url, pairedKey, CancellationToken.None);
            Check("a paired reader learns its name", paired.Paired && paired.ReaderName == "Front counter");

            var stranger = await client.HelloAsync(url, strangerKey, CancellationToken.None);
            Check("an unpaired reader learns its code", !stranger.Paired && stranger.PairCode == "482913");

            var heard = await client.ReadAsync(url, pairedKey, "37B93304", ["https://cafe.example.com/g/ABC"], CancellationToken.None);
            Check("a read says how many screens heard it", heard == 2);

            // The mark is what stops an all-digit UID being taken for a stripe number.
            var sent = posted.TryDequeue(out var body) ? body : default;
            Check("the chip is sent marked as a chip",
                sent.ValueKind == JsonValueKind.Object && sent.GetProperty("serial").GetString() == "chip:37B93304");
            Check("the card's records go with it",
                sent.ValueKind == JsonValueKind.Object && sent.GetProperty("records")[0].GetString() == "https://cafe.example.com/g/ABC");

            Check("a read from an unpaired reader is null, not an error",
                await client.ReadAsync(url, strangerKey, "37B93304", [], CancellationToken.None) is null);

            // Nothing listening there: the tray says "can't reach", it does not crash.
            var unreachable = false;
            try { await client.HelloAsync("http://127.0.0.1:1", pairedKey, CancellationToken.None); }
            catch (HttpRequestException ex) { unreachable = ShopClient.Reason(ex) == "no connection"; }
            Check("a shop that isn't there is \"no connection\"", unreachable);
        }
        finally
        {
            await shop.DisposeAsync();
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> Post(HttpClient http, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(path, content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // ---- harness ---------------------------------------------------------

    private static PrintJob Job(string deviceKey, string jobId = "job") => new()
    {
        DeviceKey = deviceKey,
        DeviceLabel = deviceKey,
        Destination = Destination("d", DestinationKind.Raw, null),
        ContentType = ContentTypes.Raw,
        Payload = [0x41],
        OpenDrawer = false,
        JobId = jobId,
        CallerSuppliedJobId = true,
        Label = jobId,
        Copies = 1
    };

    private static Destination Destination(
        string name, DestinationKind kind, string? drawerCommand, int? pulseMs = null) => new()
    {
        Name = name,
        Queue = "test-queue",
        Kind = kind,
        DrawerCommand = drawerCommand,
        DrawerPulseMs = pulseMs
    };

    private static void Section(string title)
    {
        Lines.Add($"-- {title}");
    }

    private static void Hex(string what, ReadOnlyMemory<byte> actual, string expected)
    {
        var got = Convert.ToHexString(actual.ToArray());
        if (string.Equals(got, expected, StringComparison.OrdinalIgnoreCase))
        {
            Pass($"{what} = {got}");
            return;
        }

        Fail(what, $"expected {expected}, got {got}");
    }

    private static void Check(string what, bool condition)
    {
        if (condition) Pass(what);
        else Fail(what, "false");
    }

    private static void Pass(string what)
    {
        _passed++;
        Lines.Add($"   ok   {what}");
    }

    private static void Fail(string what, string detail)
    {
        _failed++;
        Lines.Add($"   FAIL {what}: {detail}");
    }
}
