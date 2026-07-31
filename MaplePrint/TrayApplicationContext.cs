using System.Runtime.Versioning;
using MaplePrint.Diagnostics;
using MaplePrint.Printing;
using MaplePrint.Server;
using MaplePrint.Settings;

namespace MaplePrint;

/// <summary>
/// The whole application. MaplePrint has no main window on purpose — it is
/// unattended infrastructure that should start with the PC and be forgotten,
/// so it lives in the notification area and only shows a window when someone
/// goes looking for one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _testMenu = new("&Test print");
    private readonly ToolStripMenuItem _labelMenu = new("&See a label");
    private readonly ActivityLog _log;
    private readonly Control _marshal;
    private readonly PrintServer _server;

    private AppSettings _settings;
    private ActivityForm? _activityForm;

    public TrayApplicationContext()
    {
        _settings = JsonSettingsStore.Load();
        _log = new ActivityLog(JsonSettingsStore.FolderPath);

        // State arrives on Kestrel's threads; this gives us a handle on the UI
        // thread to marshal through.
        //
        // A Form whose Handle we touch but never Show, rather than a bare
        // Control: CreateControl() on a parentless Control produces a genuine
        // top-level window that the shell will happily paint as a blank grey
        // rectangle on the desktop. Touching Handle creates the window without
        // ever setting WS_VISIBLE.
        _marshal = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1)
        };
        _ = _marshal.Handle;

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };

        // Built before the menu, because handlers close over it and the
        // compiler is right to complain if it might still be null.
        _server = new PrintServer(_settings, _log);
        _server.StateChanged += OnStateChanged;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy server &address", null, (_, _) => CopyAddress());
        menu.Items.Add("Copy API &key", null, (_, _) => CopyApiKey());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("&Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("&Activity…", null, (_, _) => ShowActivity());
        menu.Items.Add(_labelMenu);
        menu.Items.Add("Saved &jobs…", null, (_, _) => ShowSavedJobs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_testMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Re&start server", null, async (_, _) => await _server.RestartAsync());
        menu.Items.Add(new ToolStripSeparator());
        // Fully qualified: an unqualified "Help" binds to System.Windows.Forms.Help.
        menu.Items.Add("&Help", null, (_, _) => MaplePrint.Help.HelpDocument.Show());
        menu.Items.Add("E&xit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "MaplePrint",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowActivity();

        // Rebuilt as the menu opens: the label worth looking at is usually the
        // one that came out of the printer thirty seconds ago.
        _labelMenu.DropDownOpening += (_, _) => RebuildLabelMenu();

        _log.Info($"MaplePrint {PrintServer.Version} started.");

        RebuildTestMenu();
        _ = _server.StartAsync();

        // No printers mapped means every job will be refused, so nudge on first
        // run — but after the server is up, not instead of it.
        if (_settings.Destinations.TrueForAll(d => !d.IsConfigured))
            BeginInvokeOnUi(ShowSettings);
    }

    private void OnStateChanged(ServerState state, string? reason)
    {
        BeginInvokeOnUi(() =>
        {
            SetStatusText(state switch
            {
                ServerState.Listening => $"Listening on {_server.ListenUrl}",
                ServerState.Starting => "Starting…",
                ServerState.Faulted => "Not listening",
                _ => "Stopped"
            });

            if (state is ServerState.Faulted && reason is not null)
                _tray.ShowBalloonTip(6000, "MaplePrint", reason, ToolTipIcon.Warning);
        });
    }

    private void SetStatusText(string text)
    {
        _statusItem.Text = $"MaplePrint — {text}";

        // NotifyIcon.Text throws above 63 characters.
        var tip = $"MaplePrint — {text}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void CopyAddress()
    {
        var url = _server.ListenUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("The server isn't listening yet.", "MaplePrint",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        TryCopy(url, "Server address copied. Paste it into the app that will print.");
    }

    private void CopyApiKey()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            MessageBox.Show(
                "No API key is set, so callers don't need one. Set a key in Settings if you "
                + "need to restrict who can print.",
                "MaplePrint", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        TryCopy(_settings.ApiKey, "API key copied. Paste it into the app that will print.");
    }

    private void TryCopy(string value, string confirmation)
    {
        try
        {
            Clipboard.SetText(value);
            _tray.ShowBalloonTip(3000, "MaplePrint", confirmation, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "MaplePrint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        var committed = form.ShowDialog() == DialogResult.OK;

        if (committed) _settings = form.Result;

        // Written either way. The dialog records its size and position as it
        // closes, and cancelling an edit shouldn't throw that away — the window
        // geometry isn't one of the settings being edited.
        JsonSettingsStore.Save(_settings);

        if (!committed) return;

        StartupRegistration.Apply(_settings.StartWithWindows);
        RebuildTestMenu();
        _log.Info("Settings saved.");

        _server.UpdateSettings(_settings);
        _ = _server.RestartAsync();
    }

    private void ShowActivity()
    {
        if (_activityForm is { IsDisposed: false })
        {
            _activityForm.Activate();
            return;
        }

        _activityForm = new ActivityForm(_log);
        _activityForm.FormClosed += (_, _) => _activityForm = null;
        _activityForm.Show();
    }

    /// <summary>
    /// Built from the configured destinations rather than hard-coded, because
    /// there is no longer a fixed pair of them — a shop can add a third printer
    /// in Settings and must be able to test it from here without a new build.
    /// </summary>
    private void RebuildTestMenu()
    {
        _testMenu.DropDownItems.Clear();

        var ready = _settings.Destinations.Where(d => d.IsConfigured).ToList();
        if (ready.Count == 0)
        {
            _testMenu.DropDownItems.Add(new ToolStripMenuItem("No printers set up yet") { Enabled = false });
            return;
        }

        foreach (var destination in ready)
        {
            // Captured per iteration; the handler must not close over the loop
            // variable's final value.
            var name = destination.Name;
            _testMenu.DropDownItems.Add($"{name}  ({Shorten(destination.Queue)})", null, (_, _) => TestPrint(name));
        }
    }

    private static string Shorten(string value) =>
        value.Length <= 34 ? value : value[..31] + "…";

    /// <summary>
    /// The last few labels, so one can be looked at. Rebuilt on open rather than
    /// cached: the whole point is to see the job that just came out wrong.
    /// </summary>
    private void RebuildLabelMenu()
    {
        _labelMenu.DropDownItems.Clear();

        var recent = JobArchive.RecentLabels(5);
        if (recent.Count == 0)
        {
            var why = _settings.SaveJobCopies
                ? "No labels printed yet"
                : "Turn on saving jobs in Settings first";
            _labelMenu.DropDownItems.Add(new ToolStripMenuItem(why) { Enabled = false });
            return;
        }

        foreach (var file in recent)
        {
            // Captured per iteration; the handler must not close over the loop
            // variable's final value.
            var path = file.FullName;
            var caption = $"{file.LastWriteTime:HH:mm:ss}  {DestinationOf(file.Name)}";
            _labelMenu.DropDownItems.Add(caption, null, async (_, _) => await ShowLabelAsync(path));
        }
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
    /// be drawn, so it happens only here, on a menu somebody clicked — never as
    /// a side effect of printing.
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

            _log.Info($"Previewed {Path.GetFileName(zplFile)}");
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
    /// Prints without going through the API — this is the first thing to try
    /// when a shop reports "nothing prints", because it splits the problem
    /// cleanly into paper-path versus caller.
    /// </summary>
    private void TestPrint(string destinationName)
    {
        var destination = _settings.Find(destinationName);

        if (destination is null || !destination.IsConfigured)
        {
            MessageBox.Show($"No printer is set for '{destinationName}' yet. Choose one in Settings first.",
                "MaplePrint", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var documentName = $"MaplePrint {destination.Name} test";
        var error = destination.Kind == DestinationKind.Document
            ? PrintTestImage(destination, documentName)
            : RawPrinter.Send(destination.Queue, TestBytesFor(destination), documentName);

        if (error is null)
        {
            _log.Success($"Test {destination.Name} sent to {destination.Queue}.");
            _tray.ShowBalloonTip(3000, "MaplePrint", $"Test {destination.Name} sent to {destination.Queue}.", ToolTipIcon.Info);
        }
        else
        {
            _log.Error($"Test {destination.Name} failed: {error}");
            MessageBox.Show(error, "MaplePrint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private string? PrintTestImage(Destination destination, string documentName)
    {
        using var image = TestDocuments.CalibrationImage(_settings.StationName, destination.DotWidth, destination.RenderDpi);
        return DocumentPrinter.Print(
            destination.Queue, image, documentName,
            paperSizeName: destination.PaperSizeName,
            renderDpi: destination.RenderDpi,
            note: _log.Info);
    }

    /// <summary>
    /// Chosen by the destination's declared language rather than its name, so a
    /// second ZPL printer called anything at all still gets a ZPL self-test.
    /// </summary>
    private byte[] TestBytesFor(Destination destination) => destination.Language switch
    {
        PrinterLanguages.Zpl => TestDocuments.Label(_settings.StationName),
        _ => TestDocuments.Receipt(_settings.StationName)
    };

    /// <summary>
    /// Pulls the maple leaf out of our own executable's Win32 resources, which
    /// <c>&lt;ApplicationIcon&gt;</c> put there at build time. Reading it back this
    /// way keeps the published artifact a single .exe with nothing beside it.
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch { }

        return SystemIcons.Application;
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
                + "in Settings, print something, then look here again.\n\n"
                + "A saved .zpl can be opened at labelary.com to see the label without printing one.",
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

    private void BeginInvokeOnUi(Action action)
    {
        if (_marshal.IsHandleCreated && !_marshal.IsDisposed) _marshal.BeginInvoke(action);
        else action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Hide before disposing or the icon lingers in the tray until the
            // user hovers over it.
            _tray.Visible = false;
            _server.StateChanged -= OnStateChanged;
            _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _tray.Dispose();
            _marshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
