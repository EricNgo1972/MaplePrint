using System.Text.RegularExpressions;
using MaplePrint.Server;
using MaplePrint.Settings;

namespace MaplePrint.Printing;

/// <summary>
/// Keeps a copy of what callers send, so "what did it actually print?" can be
/// answered from this PC instead of guessed at from the other end.
///
/// <para>A saved <c>.zpl</c> opens in labelary.com and renders exactly what the
/// printer would have — which is how a label design gets checked without a
/// label printer, or a sticker. A saved <c>.png</c> is the rendered page a
/// document destination was given.</para>
///
/// <para>Bounded on purpose. This runs on a till that stays on for months, and a
/// diagnostic that quietly fills a disk is a worse fault than the one it was
/// turned on to find.</para>
/// </summary>
public static class JobArchive
{
    /// <summary>Jobs kept before the oldest are dropped.</summary>
    private const int KeepFiles = 200;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MaplePrint",
        "jobs");

    /// <summary>
    /// Writes one job and returns its path, or null if it wasn't saved. Never
    /// throws: a diagnostic must not be able to fail a print.
    /// </summary>
    public static string? Save(Destination destination, string contentType, byte[] payload, DateTime when)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            var path = Path.Combine(
                Folder,
                $"{when:yyyyMMdd-HHmmss-fff}-{Safe(destination.Name)}{Extension(destination, contentType)}");

            File.WriteAllBytes(path, payload);
            Prune();
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The most recently saved labels, newest first. Only ZPL: those are the
    /// ones that can be turned back into a picture, and a list mixing in files
    /// that can't be previewed would just be a list of disappointments.
    /// </summary>
    public static IReadOnlyList<FileInfo> RecentLabels(int count)
    {
        try
        {
            return new DirectoryInfo(Folder)
                .GetFiles("*.zpl")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(count)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// The extension that makes the file open in something useful: the printer's
    /// language for raw jobs, and the image format for rendered ones.
    /// </summary>
    private static string Extension(Destination destination, string contentType)
    {
        if (destination.Kind != DestinationKind.Raw)
            return contentType == ContentTypes.Image ? ".png" : ".bin";

        return string.Equals(destination.Language, PrinterLanguages.Zpl, StringComparison.OrdinalIgnoreCase)
            ? ".zpl"
            : ".escpos";
    }

    /// <summary>Destination names reach us over HTTP, so they are not allowed
    /// anywhere near a path unfiltered.</summary>
    private static string Safe(string name)
    {
        var cleaned = Regex.Replace(name ?? "", "[^A-Za-z0-9_-]", "");
        return cleaned.Length == 0 ? "job" : cleaned;
    }

    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles();
            if (files.Length <= KeepFiles) return;

            foreach (var stale in files.OrderByDescending(f => f.CreationTimeUtc).Skip(KeepFiles))
                try { stale.Delete(); } catch { }
        }
        catch { }
    }
}
