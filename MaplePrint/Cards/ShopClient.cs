using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MaplePrint.Cards;

/// <summary>
/// The two calls a Maple shop's card-reader endpoints take — the same two its phone readers make,
/// because to the shop this PC's card reader is one of them (<c>CardReaderKind.Agent</c>): it says
/// hello to learn whether it is paired, and posts what it read. The shop hands each read to every
/// counter screen the reader is attached to, wherever that screen is.
///
/// <para>Told the shop and the key on every call rather than holding them, so a change in Settings
/// takes effect on the next call with nothing to rebuild.</para>
/// </summary>
internal sealed class ShopClient : IDisposable
{
    private const string Api = "/card-reader/api/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Whether the shop knows this reader, or the code to show until it does.</summary>
    /// <exception cref="HttpRequestException">The shop can't be reached, or isn't a shop with card readers.</exception>
    public async Task<Hello> HelloAsync(string shopUrl, string key, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(shopUrl + Api + "hello", new { key }, Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Hello>(Json, ct) ?? throw new HttpRequestException("Empty answer from the shop.");
    }

    /// <summary>
    /// Hands a read to the shop. The chip's ID goes as the serial, marked as a chip so the shop never
    /// takes a UID with no letters for a stripe number; the card's NDEF texts go as its records,
    /// exactly as a phone sends them.
    /// </summary>
    /// <returns>How many counter screens heard it; null when the shop says this reader is not paired.</returns>
    public async Task<int?> ReadAsync(string shopUrl, string key, string uid, IReadOnlyList<string> records, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(shopUrl + Api + "read", new { key, serial = ChipPrefix + uid, records }, Json, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReadAnswer>(Json, ct))?.Heard ?? 0;
    }

    /// <summary>What marks a read as a chip's ID to the shop (<c>CardKeys.ChipPrefix</c>).</summary>
    public const string ChipPrefix = "chip:";

    /// <summary>Why a call failed, in words for the tray and the log.</summary>
    public static string Reason(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "that address has no card readers",
        HttpRequestException { StatusCode: { } status } => $"the shop answered {(int)status}",
        TaskCanceledException => "no answer",
        _ => "no connection",
    };

    public void Dispose() => _http.Dispose();

    public sealed record Hello(bool Paired, string? PairCode, string? ReaderName);

    private sealed record ReadAnswer(int Heard);
}
