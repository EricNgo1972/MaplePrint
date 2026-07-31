using System.Runtime.Versioning;
using MaplePrint.Diagnostics;
using MaplePrint.Printing;
using MaplePrint.Settings;

namespace MaplePrint;

/// <summary>
/// A live view of what the agent has been doing. This is the window a manager
/// opens when a customer says their receipt never printed — it answers "did the
/// job arrive at all?", which is the fork that decides who to call.
///
/// <para>The saved-job tools live here rather than on the tray menu. They
/// answer the next question after that fork — "it arrived, but did it come out
/// right?" — and a tray menu earns its place by staying short enough to read at
/// a glance.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ActivityForm : Form
{
    private readonly ActivityLog _log;
    private readonly AppSettings _settings;
    private readonly ListBox _list;

    public ActivityForm(ActivityLog log, AppSettings settings)
    {
        _log = log;
        _settings = settings;

        Text = "MaplePrint — Activity";
        Width = 900;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 280);

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            IntegralHeight = false,
            HorizontalScrollbar = true
        };

        // Measured, not sized in pixels. Five buttons of hand-picked widths fit
        // at one font scale and clip at the next — which is what a button
        // reading "See a" instead of "See a label…" was.
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(8)
        };

        var close = Footer("Close");
        close.DialogResult = DialogResult.Cancel;
        var openFile = Footer("Open log file");
        var copy = Footer("Copy all");
        var seeLabel = Footer("See a label…");
        var savedJobs = Footer("Saved jobs");

        close.Click += (_, _) => Close();
        copy.Click += (_, _) => CopyAll();
        openFile.Click += (_, _) => OpenLogFile();
        savedJobs.Click += (_, _) => ShowSavedJobs();

        // The list is built as it drops, not once: the label worth looking at is
        // usually the one that printed thirty seconds ago.
        seeLabel.Click += (_, _) => ShowRecentLabels(seeLabel);

        bottom.Controls.Add(close);
        bottom.Controls.Add(openFile);
        bottom.Controls.Add(copy);
        bottom.Controls.Add(seeLabel);
        bottom.Controls.Add(savedJobs);

        Controls.Add(_list);
        Controls.Add(bottom);
        CancelButton = close;

        // The window may not be allowed to get narrower than its own buttons.
        // Measured for the same reason they are: five of them at a large font
        // scale need more room than any number typed here would have guessed.
        var needed = bottom.PreferredSize.Width + (Width - ClientSize.Width) + 16;
        MinimumSize = new Size(Math.Max(MinimumSize.Width, needed), MinimumSize.Height);

        foreach (var entry in _log.Snapshot()) _list.Items.Add(entry.ToString());
        ScrollToEnd();

        _log.Appended += OnAppended;
        FormClosed += (_, _) => _log.Appended -= OnAppended;
    }

    /// <summary>A footer button that is as wide as its own text, with a minimum
    /// so short labels don't come out cramped beside long ones.</summary>
    private static Button Footer(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(84, 0),
        Padding = new Padding(10, 4, 10, 4),
        Margin = new Padding(4, 3, 4, 3)
    };

    private void OnAppended(ActivityEntry entry)
    {
        if (IsDisposed || !IsHandleCreated) return;

        // Raised from connection threads — hop to the UI thread before touching
        // the list.
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            _list.Items.Add(entry.ToString());
            while (_list.Items.Count > 500) _list.Items.RemoveAt(0);
            ScrollToEnd();
        });
    }

    private void ScrollToEnd()
    {
        if (_list.Items.Count > 0) _list.TopIndex = _list.Items.Count - 1;
    }

    private void CopyAll()
    {
        if (_list.Items.Count == 0) return;
        var text = string.Join(Environment.NewLine, _list.Items.Cast<object>().Select(i => i.ToString()));
        try { Clipboard.SetText(text); } catch { }
    }

    /// <summary>
    /// The last few labels, as a menu under the button. Five, because this is a
    /// "what just happened" tool — anything older is a job for the folder.
    /// </summary>
    private void ShowRecentLabels(Control anchor)
    {
        var menu = new ContextMenuStrip();
        var recent = JobArchive.RecentLabels(5);

        if (recent.Count == 0)
        {
            var why = _settings.SaveJobCopies
                ? "No labels printed yet"
                : "Turn on saving jobs in Settings first";
            menu.Items.Add(new ToolStripMenuItem(why) { Enabled = false });
        }
        else
        {
            foreach (var file in recent)
            {
                // Captured per iteration; the handler must not close over the
                // loop variable's final value.
                var path = file.FullName;
                menu.Items.Add(
                    $"{file.LastWriteTime:HH:mm:ss}  {DestinationOf(file.Name)}",
                    null,
                    async (_, _) => await ShowLabelAsync(path));
            }
        }

        menu.Show(anchor, new Point(0, -menu.PreferredSize.Height));
    }

    /// <summary>The destination out of a saved job's file name, which is stamped
    /// "20260731-144512-345-label.zpl".</summary>
    private static string DestinationOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dash = stem.LastIndexOf('-');
        return dash >= 0 && dash < stem.Length - 1 ? stem[(dash + 1)..] : stem;
    }

    /// <summary>
    /// Renders one saved label and opens the picture. The job leaves this PC to
    /// be drawn, so it happens only here, on a button somebody pressed — never
    /// as a side effect of printing.
    /// </summary>
    private async Task ShowLabelAsync(string zplFile)
    {
        try
        {
            var dpi = _settings.Destinations
                .FirstOrDefault(d => string.Equals(
                    d.Name, DestinationOf(Path.GetFileName(zplFile)), StringComparison.OrdinalIgnoreCase))
                ?.RenderDpi ?? 0;

            var png = await LabelPreview.RenderAsync(zplFile, dpi);

            using var viewer = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(png) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not draw that label.\n\n{ex.Message}\n\n"
                + "Drawing it needs an internet connection — the label is rendered by labelary.com.",
                "MaplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Opens the folder saved jobs land in. Says so plainly when the setting is
    /// off rather than opening an empty folder, which would read as "nothing
    /// printed" instead of "nothing was being kept".
    /// </summary>
    private void ShowSavedJobs()
    {
        if (!_settings.SaveJobCopies && !Directory.Exists(JobArchive.Folder))
        {
            MessageBox.Show(
                "Jobs aren't being saved. Turn on \"Keep a copy of each job for troubleshooting\" "
                + "in Settings, print something, then look here again.",
                "MaplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(JobArchive.Folder);
            using var explorer = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(JobArchive.Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {JobArchive.Folder}: {ex.Message}", "MaplePrint");
        }
    }

    private void OpenLogFile()
    {
        try
        {
            if (!File.Exists(_log.FilePath))
            {
                MessageBox.Show("No log file has been written yet.", "MaplePrint");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _log.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "MaplePrint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
