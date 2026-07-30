using System.Runtime.Versioning;
using MaplePrint.Printing;
using MaplePrint.Settings;

namespace MaplePrint;

/// <summary>
/// One-time setup per PC: which port to listen on, the key callers must
/// present, and which Windows queue plays each role.
///
/// There is deliberately nowhere to type another application's address. Callers
/// are configured with MaplePrint's address and key — copied out of this
/// dialog — so this PC never needs to know who prints through it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
    private readonly NumericUpDown _listenPort = new() { Minimum = 1, Maximum = 65535, ThousandsSeparator = false };
    private readonly TextBox _apiKey = new() { ReadOnly = true };
    private readonly TextBox _stationName = new();
    private readonly ComboBox _receiptQueue = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _labelQueue = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly CheckBox _allowRemote = new() { Text = "Accept jobs from other machines on the network", AutoSize = true };
    private readonly CheckBox _startWithWindows = new() { Text = "Start MaplePrint when I sign in", AutoSize = true };

    private string _key;

    public SettingsForm(AppSettings current)
    {
        Result = current;
        _key = string.IsNullOrWhiteSpace(current.ApiKey) ? AppSettings.NewApiKey() : current.ApiKey;

        Text = "MaplePrint — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(600, 480);

        var printers = PrinterQueues.Installed().ToArray();
        _receiptQueue.Items.AddRange(printers);
        _labelQueue.Items.AddRange(printers);

        _listenPort.Value = current.ListenPort is > 0 and < 65536 ? current.ListenPort : AppSettings.DefaultPort;
        _apiKey.Text = _key;
        _stationName.Text = string.IsNullOrWhiteSpace(current.StationName) ? Environment.MachineName : current.StationName;
        _receiptQueue.Text = current.ReceiptQueue;
        _labelQueue.Text = current.LabelQueue;
        _allowRemote.Checked = current.AllowRemote;
        _startWithWindows.Checked = current.StartWithWindows || StartupRegistration.IsEnabled();

        // Fill, not Top: the buttons dock to the bottom edge first and this takes
        // whatever is left. AutoScroll rather than AutoSize so a large Windows
        // text-scaling setting pushes a scrollbar instead of clipping Save.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(14)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string caption, Control control, int width = 400)
        {
            layout.Controls.Add(new Label
            {
                Text = caption,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 7, 3, 3)
            });
            control.Width = width;
            control.Margin = new Padding(3, 4, 3, 4);
            layout.Controls.Add(control);
        }

        Row("Listen on port", _listenPort, 100);
        Row("API key", _apiKey);

        var keyButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(3, 0, 3, 6) };
        var copyKey = new Button { Text = "Copy key", Width = 100 };
        var newKey = new Button { Text = "Generate new", Width = 110 };
        keyButtons.Controls.Add(copyKey);
        keyButtons.Controls.Add(newKey);
        layout.Controls.Add(new Label());
        layout.Controls.Add(keyButtons);

        Row("Station name", _stationName);
        Row("Receipt printer", _receiptQueue);
        Row("Label printer", _labelQueue);

        layout.Controls.Add(new Label());
        layout.Controls.Add(_allowRemote);
        layout.Controls.Add(new Label());
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Off, only this PC can send jobs. On, Windows Firewall may also need to "
                 + "allow the port before another machine can reach it."
        });
        layout.Controls.Add(new Label());
        layout.Controls.Add(_startWithWindows);

        var save = new Button { Text = "Save", Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(10)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        copyKey.Click += (_, _) => { try { Clipboard.SetText(_key); } catch { } };
        newKey.Click += (_, _) => RegenerateKey();
        save.Click += OnSave;
    }

    /// <summary>Valid once the dialog returns <see cref="DialogResult.OK"/>.</summary>
    public AppSettings Result { get; private set; }

    private void RegenerateKey()
    {
        var confirm = MessageBox.Show(
            "Generate a new key?\n\nEvery app currently printing through MaplePrint will stop "
            + "working until you paste the new key into it.",
            "MaplePrint", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        _key = AppSettings.NewApiKey();
        _apiKey.Text = _key;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        Result = new AppSettings
        {
            ListenPort = (int)_listenPort.Value,
            ApiKey = _key,
            AllowRemote = _allowRemote.Checked,
            StationName = string.IsNullOrWhiteSpace(_stationName.Text) ? Environment.MachineName : _stationName.Text.Trim(),
            ReceiptQueue = _receiptQueue.Text.Trim(),
            LabelQueue = _labelQueue.Text.Trim(),
            StartWithWindows = _startWithWindows.Checked
        };
    }
}
