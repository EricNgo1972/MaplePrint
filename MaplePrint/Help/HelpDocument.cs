using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace MaplePrint.Help;

/// <summary>
/// Opens the README in the user's browser.
///
/// The document is embedded in the executable rather than fetched, because the
/// machine that needs it most is a counter PC whose printing has stopped — and
/// quite possibly whose internet has too. Rendered to HTML rather than dropped
/// as a .md file: Windows has no default handler for Markdown, so a .md would
/// open the "how do you want to open this file?" dialog, which is not help.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HelpDocument
{
    private const string ResourceName = "MaplePrint.README.md";

    public static void Show()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "MaplePrint-help.html");
            File.WriteAllText(path, BuildHtml(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the help page: {ex.Message}",
                "MaplePrint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string ReadMarkdown()
    {
        using var stream = typeof(HelpDocument).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return "# MaplePrint\n\nThe help document was not included in this build.";

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string BuildHtml()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MaplePrint {version} — Help</title>
            <style>{Css}</style>
            </head>
            <body><main>{ToHtml(ReadMarkdown())}</main></body>
            </html>
            """;
    }

    private const string Css = """
        :root { color-scheme: light dark; }
        body { margin: 0; padding: 2.5rem 1.5rem 6rem;
               font: 15px/1.65 "Segoe UI", system-ui, sans-serif;
               background: #fff; color: #1c1c1c; }
        main { max-width: 46rem; margin: 0 auto; }
        h1 { font-size: 1.9rem; margin: 0 0 1.2rem; }
        h2 { font-size: 1.35rem; margin: 2.4rem 0 .8rem; padding-bottom: .3rem;
             border-bottom: 1px solid #e2e2e2; }
        h3 { font-size: 1.08rem; margin: 1.8rem 0 .5rem; }
        h4 { font-size: 1rem; margin: 1.4rem 0 .4rem; }
        p, ul, ol { margin: .7rem 0; }
        li { margin: .25rem 0; }
        code { font-family: Consolas, "Cascadia Mono", monospace; font-size: .89em;
               background: #f2f2f2; padding: .12em .35em; border-radius: 3px; }
        pre { background: #f6f6f6; border: 1px solid #e4e4e4; border-radius: 6px;
              padding: .85rem 1rem; overflow-x: auto; }
        pre code { background: none; padding: 0; font-size: .86em; line-height: 1.5; }
        table { border-collapse: collapse; margin: 1rem 0; width: 100%;
                display: block; overflow-x: auto; }
        th, td { border: 1px solid #e0e0e0; padding: .45rem .7rem; text-align: left;
                 vertical-align: top; }
        th { background: #f6f6f6; font-weight: 600; }
        a { color: #0b64c8; }
        strong { font-weight: 600; }
        @media (prefers-color-scheme: dark) {
          body { background: #1b1b1b; color: #e6e6e6; }
          h2 { border-bottom-color: #333; }
          code { background: #2a2a2a; }
          pre { background: #232323; border-color: #333; }
          th, td { border-color: #363636; }
          th { background: #262626; }
          a { color: #6cb6ff; }
        }
        """;

    /// <summary>
    /// A deliberately small Markdown subset — headings, fenced code, pipe
    /// tables, lists, and inline emphasis — which is all this one document
    /// uses. Not a general converter, and not trying to be: pulling in a
    /// Markdown library to render a single embedded file would cost more than
    /// it returns.
    /// </summary>
    public static string ToHtml(string markdown)
    {
        var html = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        // Soft-wrapped text is buffered and formatted as a block rather than
        // line by line, because emphasis routinely straddles a wrap: a **bold
        // run** whose closing marker is on the next source line would otherwise
        // never match, and the asterisks would print literally.
        var pending = new StringBuilder();

        var inCode = false;
        var inParagraph = false;
        var listTag = "";
        var itemOpen = false;

        void Flush()
        {
            if (pending.Length == 0) return;
            html.Append(Inline(pending.ToString()));
            pending.Clear();
        }

        void Add(string text)
        {
            if (pending.Length > 0) pending.Append(' ');
            pending.Append(text);
        }

        void CloseItem() { if (itemOpen) { Flush(); html.Append("</li>\n"); itemOpen = false; } }
        void CloseList() { CloseItem(); if (listTag.Length > 0) { html.Append($"</{listTag}>\n"); listTag = ""; } }
        void CloseParagraph() { if (inParagraph) { Flush(); html.Append("</p>\n"); inParagraph = false; } }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                if (inCode) { html.Append("</code></pre>\n"); inCode = false; }
                else { CloseParagraph(); CloseList(); html.Append("<pre><code>"); inCode = true; }
                continue;
            }

            if (inCode) { html.Append(Escape(raw)).Append('\n'); continue; }

            if (trimmed.Length == 0) { CloseParagraph(); CloseList(); continue; }

            var level = 0;
            while (level < trimmed.Length && trimmed[level] == '#') level++;
            if (level is > 0 and <= 4 && level < trimmed.Length && trimmed[level] == ' ')
            {
                CloseParagraph();
                CloseList();
                html.Append($"<h{level}>{Inline(trimmed[(level + 1)..])}</h{level}>\n");
                continue;
            }

            // Trimmed, not raw: tables are frequently indented under a numbered
            // setup step, and one indented by three spaces is still a table.
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                CloseParagraph();
                CloseList();
                i = EmitTable(html, lines, i);
                continue;
            }

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                CloseParagraph();
                if (listTag != "ul") { CloseList(); html.Append("<ul>\n"); listTag = "ul"; }
                CloseItem();
                html.Append("<li>");
                Add(trimmed[2..]);
                itemOpen = true;
                continue;
            }

            var ordered = Regex.Match(trimmed, @"^(\d+)\.\s+(.*)$");
            if (ordered.Success)
            {
                CloseParagraph();
                if (listTag != "ol") { CloseList(); html.Append("<ol>\n"); listTag = "ol"; }
                CloseItem();
                html.Append("<li>");
                Add(ordered.Groups[2].Value);
                itemOpen = true;
                continue;
            }

            // An indented line under an open list item continues it rather than
            // starting a paragraph, which is how the setup steps are written.
            if (itemOpen && (raw.StartsWith("  ") || raw.StartsWith("\t")))
            {
                Add(trimmed);
                continue;
            }

            if (!inParagraph) { CloseList(); html.Append("<p>"); inParagraph = true; }
            Add(trimmed);
        }

        if (inCode) html.Append("</code></pre>\n");
        CloseParagraph();
        CloseList();

        return html.ToString();
    }

    private static int EmitTable(StringBuilder html, string[] lines, int start)
    {
        html.Append("<table>\n<thead><tr>");
        foreach (var cell in SplitRow(lines[start])) html.Append($"<th>{Inline(cell)}</th>");
        html.Append("</tr></thead>\n<tbody>\n");

        var i = start + 2;
        for (; i < lines.Length && lines[i].TrimStart().StartsWith('|') && !IsTableSeparator(lines[i]); i++)
        {
            html.Append("<tr>");
            foreach (var cell in SplitRow(lines[i])) html.Append($"<td>{Inline(cell)}</td>");
            html.Append("</tr>\n");
        }

        html.Append("</tbody>\n</table>\n");
        return i - 1;
    }

    private static string[] SplitRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|') || !trimmed.Contains('-')) return false;

        return trimmed.All(c => c is '|' or '-' or ':' or ' ');
    }

    /// <summary>Escaped first, so the inline rules below can't be smuggled in through the text.</summary>
    private static string Inline(string text)
    {
        var s = Escape(text);

        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)\)", "<a href=\"$2\">$1</a>");
        s = Regex.Replace(s, @"`([^`]+)`", "<code>$1</code>");
        s = Regex.Replace(s, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<![\w*])\*([^*]+)\*(?![\w*])", "<em>$1</em>");

        return s;
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
