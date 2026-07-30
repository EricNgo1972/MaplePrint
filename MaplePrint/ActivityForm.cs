using System.Runtime.Versioning;
using MaplePrint.Diagnostics;

namespace MaplePrint;

/// <summary>
/// A live view of what the agent has been doing. This is the window a manager
/// opens when a customer says their receipt never printed — it answers "did the
/// job arrive at all?", which is the fork that decides who to call.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ActivityForm : Form
{
    private readonly ActivityLog _log;
    private readonly ListBox _list;

    public ActivityForm(ActivityLog log)
    {
        _log = log;

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

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8)
        };

        var close = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.Cancel };
        var openFile = new Button { Text = "Open log file", Width = 110 };
        var copy = new Button { Text = "Copy all", Width = 90 };

        close.Click += (_, _) => Close();
        copy.Click += (_, _) => CopyAll();
        openFile.Click += (_, _) => OpenLogFile();

        bottom.Controls.Add(close);
        bottom.Controls.Add(openFile);
        bottom.Controls.Add(copy);

        Controls.Add(_list);
        Controls.Add(bottom);
        CancelButton = close;

        foreach (var entry in _log.Snapshot()) _list.Items.Add(entry.ToString());
        ScrollToEnd();

        _log.Appended += OnAppended;
        FormClosed += (_, _) => _log.Appended -= OnAppended;
    }

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
