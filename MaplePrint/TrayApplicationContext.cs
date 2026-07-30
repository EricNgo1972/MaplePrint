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
    private readonly ActivityLog _log;
    private readonly Control _marshal;
    private readonly PrintServer _server;

    private AppSettings _settings;
    private ActivityForm? _activityForm;

    public TrayApplicationContext()
    {
        _settings = JsonSettingsStore.Load();
        _log = new ActivityLog(JsonSettingsStore.FolderPath);

        // A print server with no key can't serve, and making the operator press
        // a button to get one adds nothing — generate it on first run.
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _settings.ApiKey = AppSettings.NewApiKey();
            JsonSettingsStore.Save(_settings);
        }

        // State arrives on Kestrel's threads; this gives us a handle on the UI
        // thread to marshal through.
        _marshal = new Control();
        _marshal.CreateControl();

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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Test &receipt print", null, (_, _) => TestPrint(PrintTargets.Receipt));
        menu.Items.Add("Test &label print", null, (_, _) => TestPrint(PrintTargets.Label));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Re&start server", null, async (_, _) => await _server.RestartAsync());
        menu.Items.Add("E&xit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "MaplePrint",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowActivity();

        _log.Info($"MaplePrint {PrintServer.Version} started.");

        _ = _server.StartAsync();

        // No printers mapped means every job will be refused, so nudge on first
        // run — but after the server is up, not instead of it.
        if (string.IsNullOrWhiteSpace(_settings.ReceiptQueue) && string.IsNullOrWhiteSpace(_settings.LabelQueue))
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
            MessageBox.Show("No API key yet — open Settings.", "MaplePrint",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        if (form.ShowDialog() != DialogResult.OK) return;

        _settings = form.Result;
        JsonSettingsStore.Save(_settings);
        StartupRegistration.Apply(_settings.StartWithWindows);
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
    /// Prints without going through the API — this is the first thing to try
    /// when a shop reports "nothing prints", because it splits the problem
    /// cleanly into paper-path versus caller.
    /// </summary>
    private void TestPrint(string target)
    {
        var queue = target == PrintTargets.Receipt ? _settings.ReceiptQueue : _settings.LabelQueue;
        var what = target == PrintTargets.Receipt ? "receipt" : "label";

        if (string.IsNullOrWhiteSpace(queue))
        {
            MessageBox.Show($"No {what} printer is set yet. Choose one in Settings first.",
                "MaplePrint", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var payload = target == PrintTargets.Receipt
            ? TestDocuments.Receipt(_settings.StationName)
            : TestDocuments.Label(_settings.StationName);

        var error = RawPrinter.Send(queue, payload, $"MaplePrint {what} test");
        if (error is null)
        {
            _log.Success($"Test {what} sent to {queue}.");
            _tray.ShowBalloonTip(3000, "MaplePrint", $"Test {what} sent to {queue}.", ToolTipIcon.Info);
        }
        else
        {
            _log.Error($"Test {what} failed: {error}");
            MessageBox.Show(error, "MaplePrint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

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
