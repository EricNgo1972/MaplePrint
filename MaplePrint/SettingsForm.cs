using System.Runtime.Versioning;
using MaplePrint.Printing;
using MaplePrint.Settings;
using MaplePrint.UI;

namespace MaplePrint;

/// <summary>
/// One-time setup per PC: which port to listen on, what callers may address,
/// and optionally a key they must present.
///
/// There is deliberately nowhere to type another application's address. Callers
/// are configured with MaplePrint's address — copied from the tray menu — so
/// this PC never needs to know who prints through it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
    private const int LabelColumnWidth = 152;
    private const int FooterHeight = 56;

    private const string KindDocument = "Document";
    private const string KindRaw = "Raw";

    /// <summary>
    /// Stands in for "no printer assigned" in the Printer column. An empty
    /// combo item does not survive the round trip — the cell quietly falls
    /// through to the first real entry, so an unassigned destination came back
    /// mapped to whichever printer sorts first. A visible sentinel is both
    /// unambiguous to the code and clearer to read than a blank cell.
    /// </summary>
    private const string NoPrinter = "(none)";

    private readonly NumericUpDown _listenPort = new()
    {
        Minimum = 1,
        Maximum = 65535,
        ThousandsSeparator = false,
        Width = 90
    };

    private readonly TextBox _apiKey = new();
    private readonly TextBox _stationName = new();
    private readonly CheckBox _allowRemote = new() { Text = "Accept jobs from other machines on the network", AutoSize = true };
    private readonly CheckBox _startWithWindows = new() { Text = "Start MaplePrint when I sign in", AutoSize = true };
    private readonly DataGridView _grid = new();

    /// <summary>Below this the grid stops being usable, so the dialog refuses to get shorter.</summary>
    private const int MinGridHeight = 140;

    private readonly List<Label> _hints = [];
    private readonly TableLayoutPanel _layout;
    private readonly AppSettings _original;

    private int _row;
    private int _gridRow = -1;

    public SettingsForm(AppSettings current)
    {
        Result = current;
        _original = current;

        Text = "MaplePrint — Settings";
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(800, 880);
        BackColor = SystemColors.Window;

        _listenPort.Value = current.ListenPort is > 0 and < 65536 ? current.ListenPort : AppSettings.DefaultPort;
        _apiKey.Text = current.ApiKey;
        _apiKey.PlaceholderText = "leave blank — no key required";
        _stationName.Text = string.IsNullOrWhiteSpace(current.StationName) ? Environment.MachineName : current.StationName;
        _allowRemote.Checked = current.AllowRemote;
        _startWithWindows.Checked = current.StartWithWindows || StartupRegistration.IsEnabled();

        BuildGrid(current);

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = SystemColors.Window,
            Padding = new Padding(20, 14, 20, 16)
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        BuildSections();

        Controls.Add(_layout);
        Controls.Add(BuildFooter());

        RewrapHints();
        ApplyMinimumSize();

        // Restored last so it overrides the default size above. Saving is left
        // to the caller: the geometry must persist whether the dialog was
        // committed or cancelled, and only the caller knows when it is safe to
        // write the file.
        WindowPlacementManager.Attach(this, current, nameof(SettingsForm));
    }

    /// <summary>Valid once the dialog returns <see cref="DialogResult.OK"/>.</summary>
    public AppSettings Result { get; private set; }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RewrapHints();
    }

    /// <summary>
    /// Measures rather than guesses. The grid lives in a Percent row, and a
    /// Percent row only gets whatever the AutoSize rows leave behind — so on a
    /// dialog shortened past the content height it collapses to nothing and the
    /// buttons below it overlap the text above. Pinning the row to a minimum
    /// height, measuring, then restoring it gives the exact height below which
    /// that happens, and the form refuses to go there. Measured, so it stays
    /// right at any font scale.
    /// </summary>
    private void ApplyMinimumSize()
    {
        if (_gridRow < 0 || _gridRow >= _layout.RowStyles.Count) return;

        _layout.RowStyles[_gridRow] = new RowStyle(SizeType.Absolute, MinGridHeight);
        var needed = _layout.PreferredSize.Height;
        _layout.RowStyles[_gridRow] = new RowStyle(SizeType.Percent, 100F);

        var chrome = Height - ClientSize.Height;
        MinimumSize = new Size(720, needed + FooterHeight + chrome);
    }

    /// <summary>
    /// Hint labels wrap to an explicit width, so they have to be told the new
    /// one — otherwise they keep the width the dialog opened at and either
    /// overflow or leave a ragged column when it is resized.
    /// </summary>
    private void RewrapHints()
    {
        var width = Math.Max(220, ClientSize.Width - 48);
        foreach (var hint in _hints) hint.MaximumSize = new Size(width, 0);
    }

    // ---- Destination grid ------------------------------------------------

    private void BuildGrid(AppSettings current)
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        // Explicit height: the default plus cell padding clips the header text
        // just enough to look like a rendering fault.
        _grid.ColumnHeadersHeight = 30;
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _grid.RowTemplate.Height = 26;

        // A queue that is offline right now must still be selectable, or the
        // grid would silently drop a mapping the moment a printer is unplugged.
        var queues = PrinterQueues.Installed()
            .Concat(current.Destinations.Select(d => d.Queue))
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(q => q, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var enabled = new DataGridViewCheckBoxColumn { HeaderText = "On", Width = 44, ToolTipText = "Accept jobs for this destination" };
        var name = new DataGridViewTextBoxColumn { HeaderText = "Name", Width = 100, ToolTipText = "What callers put in \"destination\"" };

        var queue = new DataGridViewComboBoxColumn
        {
            HeaderText = "Printer",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox
        };
        queue.Items.Add(NoPrinter);
        queue.Items.AddRange(queues);

        var kind = new DataGridViewComboBoxColumn
        {
            HeaderText = "Kind",
            // Wide enough for "Document" plus the dropdown arrow; narrower and
            // it renders as "Docum...", which reads as a bug rather than a value.
            Width = 142,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            ToolTipText = "Document = send a rendered page. Raw = send device bytes (ZPL, ESC/POS)."
        };
        kind.Items.AddRange(KindDocument, KindRaw);

        var dots = new DataGridViewTextBoxColumn { HeaderText = "Dots", Width = 58, ToolTipText = "Printable width in dots, advertised to callers" };
        var dpi = new DataGridViewTextBoxColumn { HeaderText = "DPI", Width = 48, ToolTipText = "Resolution the caller's artwork is drawn at" };
        var paper = new DataGridViewTextBoxColumn { HeaderText = "Paper form", Width = 118, ToolTipText = "Blank = the driver's own setting. Or a form name, or \"auto\"." };

        _grid.Columns.AddRange(enabled, name, queue, kind, dots, dpi, paper);

        foreach (var d in current.Destinations) AddRow(d);

        // A queue or kind that isn't in the list would otherwise raise a modal
        // DataError at the user; there is nothing they could do about it.
        _grid.DataError += (_, e) => e.ThrowException = false;
    }

    private void AddRow(Destination d) =>
        _grid.Rows.Add(
            d.Enabled,
            d.Name,
            string.IsNullOrWhiteSpace(d.Queue) ? NoPrinter : d.Queue,
            d.Kind == DestinationKind.Raw ? KindRaw : KindDocument,
            d.DotWidth > 0 ? d.DotWidth.ToString() : "",
            d.RenderDpi > 0 ? d.RenderDpi.ToString() : Destination.DefaultRenderDpi.ToString(),
            d.PaperSizeName ?? "");

    private Control GridButtons()
    {
        var host = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Padding.Empty,
            Margin = new Padding(0, 6, 0, 0)
        };

        var add = new Button { Text = "Add", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        var remove = new Button { Text = "Remove", AutoSize = true };

        add.Click += (_, _) => AddRow(new Destination
        {
            Name = "",
            Kind = DestinationKind.Document,
            DotWidth = DotWidths.Receipt80mm,
            RenderDpi = Destination.DefaultRenderDpi
        });

        remove.Click += (_, _) =>
        {
            if (_grid.CurrentRow is { } row && !row.IsNewRow) _grid.Rows.Remove(row);
        };

        host.Controls.Add(add);
        host.Controls.Add(remove);
        return host;
    }

    // ---- Layout ----------------------------------------------------------

    private void BuildSections()
    {
        Section("Server", first: true);
        Field("Station name", _stationName);
        Field("Listen on port", Shrink(_listenPort));
        FullWidth(_allowRemote);
        Hint("Off: only this PC can send jobs. On: Windows Firewall may need to allow the port.");

        Section("Destinations");
        Hint("Names callers address, and what each one maps to here. Document = send a rendered "
           + "page; Raw = send device bytes such as ZPL.");
        _gridRow = _row;
        AddSpanning(_grid, grow: true);
        AddSpanning(GridButtons());

        Section("Security");
        Field("API key", _apiKey);
        AddSpanning(KeyButtons());
        Hint("Blank means no key is needed — normal on a shop LAN. Set one if this port is "
           + "reachable from outside it.");

        Section("Startup");
        FullWidth(_startWithWindows);
    }

    /// <summary>A bold heading with a hairline under it, spanning both columns.</summary>
    private void Section(string title, bool first = false)
    {
        AddSpanning(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = SystemColors.Highlight,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            Margin = new Padding(0, first ? 0 : 18, 0, 2)
        });

        AddSpanning(new Panel
        {
            Height = 1,
            Dock = DockStyle.Top,
            BackColor = SystemColors.ControlLight,
            Margin = new Padding(0, 0, 0, 8)
        });
    }

    private void Field(string caption, Control control)
    {
        _layout.Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0)
        }, 0, _row);

        if (control.Dock == DockStyle.None && control is not Panel) control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 3, 0, 3);
        _layout.Controls.Add(control, 1, _row);

        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _row++;
    }

    private void FullWidth(Control control)
    {
        control.Margin = new Padding(0, 4, 0, 2);
        AddSpanning(control);
    }

    private void Hint(string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 6)
        };

        _hints.Add(label);
        AddSpanning(label);
    }

    /// <summary>
    /// Rows are added explicitly rather than left to grow implicitly, because
    /// exactly one of them — the grid — has to take the slack when the dialog
    /// is resized, and that needs a RowStyle of its own.
    /// </summary>
    private void AddSpanning(Control control, bool grow = false)
    {
        _layout.Controls.Add(control, 0, _row);
        _layout.SetColumnSpan(control, 2);
        _layout.RowStyles.Add(grow ? new RowStyle(SizeType.Percent, 100F) : new RowStyle(SizeType.AutoSize));
        _row++;
    }

    /// <summary>Keeps a narrow control narrow inside a stretched column.</summary>
    private static Control Shrink(Control inner)
    {
        var host = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 1, 0, 1),
            Padding = Padding.Empty
        };
        inner.Margin = Padding.Empty;
        host.Controls.Add(inner);
        return host;
    }

    private Control KeyButtons()
    {
        var host = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Padding.Empty,
            Margin = new Padding(LabelColumnWidth, 2, 0, 0)
        };

        var generate = new Button { Text = "Generate", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        var copy = new Button { Text = "Copy", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        var clear = new Button { Text = "Clear", AutoSize = true };

        generate.Click += (_, _) => _apiKey.Text = AppSettings.NewApiKey();
        copy.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(_apiKey.Text)) { try { Clipboard.SetText(_apiKey.Text.Trim()); } catch { } } };
        clear.Click += (_, _) => _apiKey.Clear();

        host.Controls.Add(generate);
        host.Controls.Add(copy);
        host.Controls.Add(clear);
        return host;
    }

    private Control BuildFooter()
    {
        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(88, 28), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(88, 28), DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };

        save.Click += OnSave;
        AcceptButton = save;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(20, 12, 20, 12),
            BackColor = SystemColors.Control
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        // Help sits apart from the commit buttons, on the opposite side, so it
        // can't be hit while reaching for Save.
        var help = new Button { Text = "Help", AutoSize = true, MinimumSize = new Size(88, 28) };
        help.Click += (_, _) => MaplePrint.Help.HelpDocument.Show();

        var helpHost = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20, 12, 0, 12),
            BackColor = SystemColors.Control
        };
        helpHost.Controls.Add(help);

        // Added innermost-first: the hairline claims the top edge, Help the
        // left, and the commit buttons fill what remains.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = FooterHeight, BackColor = SystemColors.Control };
        footer.Controls.Add(buttons);
        footer.Controls.Add(helpHost);
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark });
        return footer;
    }

    // ---- Save ------------------------------------------------------------

    private void OnSave(object? sender, EventArgs e)
    {
        // Commit whatever cell is mid-edit, or its value is lost on Save.
        _grid.EndEdit();

        if (!TryReadDestinations(out var destinations, out var problem))
        {
            MessageBox.Show(problem, "MaplePrint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        Result = new AppSettings
        {
            ListenPort = (int)_listenPort.Value,
            ApiKey = _apiKey.Text.Trim(),
            AllowRemote = _allowRemote.Checked,
            StationName = string.IsNullOrWhiteSpace(_stationName.Text) ? Environment.MachineName : _stationName.Text.Trim(),
            Destinations = destinations,
            StartWithWindows = _startWithWindows.Checked,

            // The same dictionary instance, not a copy: this runs before
            // FormClosing captures the final geometry, and sharing the reference
            // is what lets that later write reach the object we hand back.
            WindowPlacements = _original.WindowPlacements
        };
    }

    private bool TryReadDestinations(out List<Destination> destinations, out string problem)
    {
        destinations = [];
        problem = "";

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;

            var name = CellText(row, 1).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                problem = "Every destination needs a name — that is what callers address.";
                return false;
            }

            if (destinations.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                problem = $"There are two destinations called '{name}'. Names must be unique.";
                return false;
            }

            if (!TryPositive(CellText(row, 4), out var dots, allowBlank: true))
            {
                problem = $"'{name}': Dots must be a whole number of printer dots, or blank.";
                return false;
            }

            if (!TryPositive(CellText(row, 5), out var dpi, allowBlank: true))
            {
                problem = $"'{name}': DPI must be a whole number, or blank.";
                return false;
            }

            var kind = string.Equals(CellText(row, 3), KindRaw, StringComparison.OrdinalIgnoreCase)
                ? DestinationKind.Raw
                : DestinationKind.Document;

            var queue = CellText(row, 2).Trim();
            if (string.Equals(queue, NoPrinter, StringComparison.OrdinalIgnoreCase)) queue = "";

            // Language isn't in the grid — it only feeds error messages — so
            // carry it from the existing entry rather than dropping it.
            var existing = _original.Find(name);

            destinations.Add(new Destination
            {
                Name = name,
                Queue = queue,
                Kind = kind,
                Language = existing?.Language,
                DotWidth = dots,
                RenderDpi = dpi > 0 ? dpi : Destination.DefaultRenderDpi,
                PaperSizeName = Blank(CellText(row, 6)),
                Enabled = row.Cells[0].Value is true
            });
        }

        return true;
    }

    private static string CellText(DataGridViewRow row, int column) =>
        row.Cells[column].Value?.ToString() ?? "";

    private static string? Blank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryPositive(string value, out int result, bool allowBlank)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return allowBlank;
        return int.TryParse(value.Trim(), out result) && result > 0;
    }
}
