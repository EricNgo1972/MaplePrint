using System.Globalization;
using System.Text.RegularExpressions;

namespace MaplePrint.Printing;

/// <summary>
/// Turns a saved ZPL job into a picture of the label, so "did that come out
/// right?" can be answered on a PC with no label printer attached — and without
/// anyone reading ZPL.
///
/// <para>Renders through labelary.com, which is the de-facto ZPL viewer. That
/// means the job leaves this PC, so it is only ever reached from a menu someone
/// deliberately clicked, never as part of printing.</para>
/// </summary>
public static class LabelPreview
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Fallback stock when the ZPL says nothing about its own size.</summary>
    private const double FallbackWidthInches = 2.0;
    private const double FallbackHeightInches = 1.0;

    /// <summary>
    /// Renders <paramref name="zplFile"/> and returns the PNG's path, or throws
    /// with something an operator can act on.
    /// </summary>
    public static async Task<string> RenderAsync(string zplFile, int dpi, CancellationToken ct = default)
    {
        var zpl = await File.ReadAllTextAsync(zplFile, ct);
        if (!zpl.Contains("^XA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That file doesn't look like ZPL.");

        if (dpi <= 0) dpi = 203;

        // The label describes its own geometry — ^PW is the width it was told to
        // print at, ^LL the length it asked the printer for. Using those means
        // the preview is the label rather than a guess at what stock it is on.
        var width = Dots(zpl, "PW") is { } pw ? pw / (double)dpi : FallbackWidthInches;
        var height = Dots(zpl, "LL") is { } ll ? ll / (double)dpi : FallbackHeightInches;

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"http://api.labelary.com/v1/printers/{Dpmm(dpi)}dpmm/labels/{width:0.##}x{height:0.##}/0/");

        // Content type set by hand: StringContent defaults to
        // "text/plain; charset=utf-8", and the charset is enough for the viewer
        // to refuse the whole request with a 415.
        using var body = new StringContent(zpl);
        body.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var response = await Http.PostAsync(url, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"The label viewer refused it ({(int)response.StatusCode}). {Shorten(detail)}");
        }

        var png = Path.Combine(
            Path.GetTempPath(), Path.GetFileNameWithoutExtension(zplFile) + ".png");

        await File.WriteAllBytesAsync(png, await response.Content.ReadAsByteArrayAsync(ct), ct);
        return png;
    }

    /// <summary>Labelary asks for dots per millimetre; printers are sold in dpi.</summary>
    private static int Dpmm(int dpi) => dpi switch
    {
        <= 160 => 6,
        <= 240 => 8,
        <= 450 => 12,
        _ => 24
    };

    private static int? Dots(string zpl, string command)
    {
        var match = Regex.Match(zpl, $@"\^{command}(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var dots) && dots > 0
            ? dots
            : null;
    }

    private static string Shorten(string value)
    {
        value = (value ?? "").Trim().ReplaceLineEndings(" ");
        return value.Length <= 160 ? value : value[..157] + "…";
    }
}
