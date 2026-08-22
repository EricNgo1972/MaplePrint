using System.Runtime.Versioning;
using MaplePrint.Diagnostics;
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

    // Raw is split by command language because that is the choice being made:
    // "Raw" alone left Language unset, and a caller reading a raw destination
    // with no language sends ESC/POS — which a Zebra prints as pages of noise.
    private const string KindRawZpl = "Raw — ZPL (Zebra)";
    private const string KindRawEscPos = "Raw — ESC/POS";

    /// <summary>
    /// Stands in for "no printer assigned" in the Printer column. An empty
    /// combo item does not survive the round trip — the cell quietly falls
    /// through to the first real entry, so an unassigned destination came back
    /// mapped to whichever printer sorts first. A visible sentinel is both
    /// unambiguous to the code and clearer to read than a blank cell.
    /// </summary>
    private const string NoPrinter = "(none)";

    /// <summary>"This printer has no cash drawer" in the Cash drawer column.
    /// Blank is what gets stored, and blank is the flag — a destination with no
    /// drawer command has no drawer.</summary>
    private const string NoDrawer = "(none)";

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
    private readonly CheckBox _saveJobCopies = new() { Text = "Keep a copy of each job for troubleshooting", AutoSize = true };
    private readonly DataGridView _grid = new();

    /// <summary>Below this the grid stops being usable, so the dialog refuses to get shorter.</summary>
    private const int MinGridHeight = 140;

    private readonly List<Label> _hints = [];
    private readonly TableLayoutPanel _layout;
    private readonly AppSettings _original;

    private int _row;
    private int _gridRow = -1;

    /// <summary>True while rows are being filled in, so the paper-picked-a-width
    /// handler doesn't fire against half-populated rows.</summary>
    private bool _populating;

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
        _saveJobCopies.Checked = current.SaveJobCopies;

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
        var name = new DataGridViewTextBoxColumn { HeaderText = "Name", Width = 120, ToolTipText = "What callers put in \"destination\"" };

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
            // Wide enough for the longest item — "Raw — ZPL (Zebra)" — plus the
            // dropdown arrow. Narrower and it renders as "Raw — ZPL (Z...", which
            // reads as a bug rather than as a value.
            Width = 190,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            ToolTipText = "Document = a rendered page, works on any printer. Raw = device bytes, straight to the printer's own language."
        };
        kind.Items.AddRange(KindDocument, KindRawZpl, KindRawEscPos);

        // What is loaded, in the words on the box. Picking one fills in Dots
        // below, which stays the stored truth — nobody knows their printer's
        // printable width in dots, and everybody knows whether they put an 80 mm
        // roll or a 2 inch label in it.
        var width = new DataGridViewComboBoxColumn
        {
            HeaderText = "Paper",
            // "80 mm receipt" and "2.2″ label" both have to fit beside the arrow.
            Width = 148,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            ToolTipText = "The roll that is loaded. Sets Dots for you."
        };
        width.Items.AddRange([.. PaperWidths.All.Select(p => p.Name)]);
        width.Items.Add(PaperWidths.Custom);

        var dots = new DataGridViewTextBoxColumn { HeaderText = "Dots", Width = 64, ToolTipText = "Printable width in dots, advertised to callers. Set by Paper, or type it for an odd printer." };
        var dpi = new DataGridViewTextBoxColumn { HeaderText = "DPI", Width = 56, ToolTipText = "Resolution the caller's artwork is drawn at" };
        var paper = new DataGridViewTextBoxColumn { HeaderText = "Paper form", Width = 132, ToolTipText = "Blank = the driver's own setting. Or a form name, or \"auto\"." };

        // The drawer is wired to the receipt printer's DK port, so it belongs to
        // a destination rather than to the station: a café has a counter printer
        // with a till under it and a kitchen printer without one.
        //
        // One control, not two. There was a Drawer tick beside this, and its
        // value was derived from this column — (none) meant unticked and a
        // command meant ticked — so the pair had to be kept in step by hand in
        // both directions, and on save the tick silently won. That is the same
        // "second way to say the same thing" the stored model rejects, where
        // blank DrawerCommand is what "no drawer" means. Picking (none) here is
        // one click, exactly as the tick was.
        var drawerCmd = new DataGridViewComboBoxColumn
        {
            HeaderText = "Cash drawer",
            // The longest item is "ESC/POS (Epson, Bixolon, Citizen, …)", and
            // truncating it hides the brand list that is the whole point of it:
            // nobody knows their command set, they know their printer's badge.
            Width = 268,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            ToolTipText = "Which language fires the drawer wired into this printer's DK port. "
                        + "(none) means there is no drawer. Prove the choice with Open drawer, "
                        + "and change it if nothing moves."
        };
        drawerCmd.Items.Add(NoDrawer);
        drawerCmd.Items.AddRange([.. PrinterCommandSets.All.Select(c => c.Title)]);

        var drawerPulse = new DataGridViewTextBoxColumn
        {
            HeaderText = "Pulse ms",
            Width = 84,
            // The range is per command set, because the pulse byte's unit is:
            // 10 ms on Star, 2 ms on ESC/POS. Stored in milliseconds either way.
            ToolTipText = "How long the solenoid is driven, in milliseconds. 200 suits most drawers; "
                        + "some latches need 250–300. Star accepts 10–1270, ESC/POS 2–510."
        };

        var drawerUnit = new DataGridViewTextBoxColumn
        {
            HeaderText = "Unit",
            Width = 56,
            ToolTipText = "Peripheral unit the printer drives: 1, or 2 for a second drawer"
        };

        // Spooled is not obeyed. Nothing here reads a reply from the printer, so
        // the only proof the command set is right is a drawer that opens — which
        // is why the test sits in the row beside the setting rather than behind a
        // save and a menu.
        var drawerTest = new DataGridViewButtonColumn
        {
            HeaderText = "",
            Text = "Open drawer",
            UseColumnTextForButtonValue = true,
            Width = 124,
            FlatStyle = FlatStyle.Standard,
            ToolTipText = "Fire this drawer now, using the settings in this row — no need to save first"
        };

        _grid.Columns.AddRange(
            enabled, name, queue, kind, width, dots, dpi, paper,
            drawerCmd, drawerPulse, drawerUnit, drawerTest);

        _populating = true;
        foreach (var d in current.Destinations) AddRow(d);
        _populating = false;

        // Picking a roll writes the dots it implies. Committed immediately so the
        // number is visible while the combo still has focus — a value that only
        // appeared after clicking away would read as not having taken.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty &&
                _grid.CurrentCell?.ColumnIndex is ColPaperWidth or ColDrawerCmd)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _grid.CellValueChanged += (_, e) =>
        {
            // Rows.Add fills cells left to right, so this fires while Dots and
            // DPI further right are still empty — and would compute from a
            // missing resolution before the real width lands.
            if (_populating || e.RowIndex < 0 || e.ColumnIndex != ColPaperWidth) return;

            var row = _grid.Rows[e.RowIndex];
            TryPositive(CellText(row, ColDpi), out var rowDpi, allowBlank: true);

            // Custom means "leave it alone" — it is what an already-typed width
            // shows as, and choosing it must not wipe that width out.
            if (PaperWidths.DotsFor(CellText(row, ColPaperWidth), rowDpi) is { } computed)
                row.Cells[ColDots].Value = computed.ToString();
        };

        // Naming a command set fills in a pulse and a unit, so that setting a
        // drawer up is one choice rather than three. 200 ms on unit 1 is the
        // common drawer, and it is legal for both sets — 10–1270 ms on Star,
        // 2–510 ms on ESC/POS, since the pulse byte's unit differs. The Open
        // drawer button beside it settles whether the choice was right.
        _grid.CellValueChanged += (_, e) =>
        {
            if (_populating || e.RowIndex < 0 || e.ColumnIndex != ColDrawerCmd) return;

            var row = _grid.Rows[e.RowIndex];

            if (DrawerCommandFromLabel(CellText(row, ColDrawerCmd)) is null)
            {
                // Cleared, not remembered. Blank is what "no drawer" is.
                row.Cells[ColDrawerPulse].Value = "";
                row.Cells[ColDrawerUnit].Value = "";
                return;
            }

            // Only when there is nothing there. A pulse somebody tuned to their
            // latch must survive switching the command set — it is stored in
            // milliseconds precisely so it means the same thing to both.
            if (string.IsNullOrWhiteSpace(CellText(row, ColDrawerPulse)))
                row.Cells[ColDrawerPulse].Value = Destination.DefaultDrawerPulseMs.ToString();

            if (string.IsNullOrWhiteSpace(CellText(row, ColDrawerUnit)))
                row.Cells[ColDrawerUnit].Value = "1";
        };

        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColDrawerTest) return;

            // Committed first so the button tests what is on screen, including a
            // command set changed a second ago. Testing the saved settings
            // instead would break the whole try-it-and-see loop.
            _grid.EndEdit();
            TestDrawer(_grid.Rows[e.RowIndex]);
        };

        // A queue or kind that isn't in the list would otherwise raise a modal
        // DataError at the user; there is nothing they could do about it.
        _grid.DataError += (_, e) => e.ThrowException = false;
    }

    // Column positions, named so the read-back below doesn't drift from the
    // order the columns were added in.
    private const int ColName = 1;
    private const int ColQueue = 2;
    private const int ColKind = 3;
    private const int ColPaperWidth = 4;
    private const int ColDots = 5;
    private const int ColDpi = 6;
    private const int ColPaperForm = 7;
    private const int ColDrawerCmd = 8;
    private const int ColDrawerPulse = 9;
    private const int ColDrawerUnit = 10;
    private const int ColDrawerTest = 11;

    private void AddRow(Destination d) =>
        _grid.Rows.Add(
            d.Enabled,
            d.Name,
            string.IsNullOrWhiteSpace(d.Queue) ? NoPrinter : d.Queue,
            KindLabel(d),
            // Shown as the paper it corresponds to, so an existing destination
            // reads as "2.2″ label" rather than as 448.
            PaperWidths.NameFor(d.DotWidth, d.RenderDpi),
            d.DotWidth > 0 ? d.DotWidth.ToString() : "",
            d.RenderDpi > 0 ? d.RenderDpi.ToString() : Destination.DefaultRenderDpi.ToString(),
            d.PaperSizeName ?? "",
            DrawerLabel(d),
            d.HasDrawer ? d.EffectiveDrawerPulseMs.ToString() : "",
            d.HasDrawer ? d.EffectiveDrawerUnit.ToString() : "");

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

        // Dots, DPI and the driver's form are how a printer is described once, by
        // whoever installs it. Setting a shop up is answering "which printer, and
        // what is in it?" — so the rest stays out of the way until someone on a
        // support call needs it.
        var advanced = new CheckBox
        {
            Text = "Advanced",
            AutoSize = true,
            Margin = new Padding(16, 4, 0, 0),
            Checked = ShouldStartAdvanced()
        };
        advanced.CheckedChanged += (_, _) => ShowAdvancedColumns(advanced.Checked);

        host.Controls.Add(add);
        host.Controls.Add(remove);
        host.Controls.Add(advanced);

        ShowAdvancedColumns(advanced.Checked);
        return host;
    }

    private void ShowAdvancedColumns(bool visible)
    {
        foreach (var column in new[]
                 { ColDots, ColDpi, ColPaperForm, ColDrawerCmd, ColDrawerPulse, ColDrawerUnit, ColDrawerTest })
            if (column < _grid.Columns.Count) _grid.Columns[column].Visible = visible;
    }

    /// <summary>
    /// Opens showing the detail when a destination holds something this dialog
    /// can't express any other way — an unrecognised width, or a driver form.
    /// Hiding a value that is in force, and that the operator would then have no
    /// way to see, is worse than one extra column.
    /// </summary>
    private bool ShouldStartAdvanced()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;

            if (!string.IsNullOrWhiteSpace(CellText(row, ColPaperForm))) return true;

            // A drawer that is set up must be visible, or nobody can see which
            // command set is in force when it stops opening.
            if (DrawerCommandFromLabel(CellText(row, ColDrawerCmd)) is not null) return true;

            if (string.Equals(CellText(row, ColPaperWidth), PaperWidths.Custom, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(CellText(row, ColDots)))
                return true;
        }

        return false;
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
        Hint("Names callers address, and what each one maps to here. Document prints a rendered "
           + "page through the Windows driver — it works on any printer and handles logos and "
           + "accents. Raw sends the printer its own language instead, which is usually the "
           + "better fit for a label printer: it takes the label's size from the printer rather "
           + "than the driver. Pick Paper to match what is loaded.");
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
        FullWidth(_saveJobCopies);
        Hint("Off by default — a job is a customer's order. On, every job is also written to "
           + Printing.JobArchive.Folder + ", newest 200 kept. A saved .zpl can be dropped into "
           + "labelary.com to see the label without printing one.");
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
            SaveJobCopies = _saveJobCopies.Checked,

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

            var name = CellText(row, ColName).Trim();
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

            if (!TryPositive(CellText(row, ColDots), out var dots, allowBlank: true))
            {
                problem = $"'{name}': Dots must be a whole number of printer dots, or blank.";
                return false;
            }

            if (!TryPositive(CellText(row, ColDpi), out var dpi, allowBlank: true))
            {
                problem = $"'{name}': DPI must be a whole number, or blank.";
                return false;
            }

            var (kind, language) = KindFromLabel(CellText(row, ColKind));

            var queue = CellText(row, ColQueue).Trim();
            if (string.Equals(queue, NoPrinter, StringComparison.OrdinalIgnoreCase)) queue = "";

            // Blank is the flag: no drawer command means no drawer, so a row
            // left on (none) simply writes nothing rather than a disabled drawer.
            var drawerCommand = DrawerCommandFromLabel(CellText(row, ColDrawerCmd));

            if (!TryPositive(CellText(row, ColDrawerPulse), out var pulse, allowBlank: true))
            {
                problem = $"'{name}': Pulse ms must be a whole number of milliseconds, or blank.";
                return false;
            }

            if (!TryPositive(CellText(row, ColDrawerUnit), out var unit, allowBlank: true))
            {
                problem = $"'{name}': Unit must be 1 or 2.";
                return false;
            }

            var destination = new Destination
            {
                Name = name,
                Queue = queue,
                Kind = kind,
                Language = language,
                DotWidth = dots,
                RenderDpi = dpi > 0 ? dpi : Destination.DefaultRenderDpi,
                PaperSizeName = Blank(CellText(row, ColPaperForm)),
                Enabled = row.Cells[0].Value is true,
                DrawerCommand = drawerCommand,
                DrawerPulseMs = drawerCommand is null || pulse <= 0 ? null : pulse,
                DrawerUnit = drawerCommand is null || unit <= 0 ? null : unit
            };

            // Caught here rather than at the counter. A pulse the command set
            // cannot express is the fault that presents as "the drawer randomly
            // does not open", which nobody traces back to a settings dialog.
            if (drawerCommand is not null && DrawerDispatch.Problem(destination) is { } drawerProblem)
            {
                problem = drawerProblem;
                return false;
            }

            destinations.Add(destination);
        }

        return true;
    }

    /// <summary>
    /// Fires the drawer described by this row, right now, without saving.
    ///
    /// <para>The point of the button: the command set is <i>declared</i>, never
    /// detected, and a Star and an Epson disagree about what a pulse byte means.
    /// Nothing reads a reply from the printer, so the only proof the declaration
    /// is right is a drawer that opens.</para>
    /// </summary>
    private void TestDrawer(DataGridViewRow row)
    {
        var name = CellText(row, ColName).Trim();
        var shown = string.IsNullOrWhiteSpace(name) ? "this destination" : name;

        var queue = CellText(row, ColQueue).Trim();
        if (string.Equals(queue, NoPrinter, StringComparison.OrdinalIgnoreCase)) queue = "";

        if (string.IsNullOrWhiteSpace(queue))
        {
            Say($"'{shown}' has no printer assigned. Pick one first — the drawer is wired into it.",
                MessageBoxIcon.Information);
            return;
        }

        if (!PrinterQueues.Exists(queue))
        {
            Say($"The queue '{queue}' is not installed on this PC.", MessageBoxIcon.Warning);
            return;
        }

        TryPositive(CellText(row, ColDrawerPulse), out var pulse, allowBlank: true);
        TryPositive(CellText(row, ColDrawerUnit), out var unit, allowBlank: true);

        var probe = new Destination
        {
            Name = shown,
            Queue = queue,
            DrawerCommand = DrawerCommandFromLabel(CellText(row, ColDrawerCmd)),
            DrawerPulseMs = pulse > 0 ? pulse : null,
            DrawerUnit = unit > 0 ? unit : null
        };

        if (DrawerDispatch.Problem(probe) is { } problem)
        {
            Say(problem, MessageBoxIcon.Warning);
            return;
        }

        var set = PrinterCommandSets.For(probe)!;
        var bytes = set.KickDrawer(probe.EffectiveDrawerPulseMs, probe.EffectiveDrawerUnit).ToArray();

        var previous = Cursor;
        Cursor = Cursors.WaitCursor;
        string? error;
        try { error = RawPrinter.Send(queue, bytes, "MaplePrint — open cash drawer"); }
        finally { Cursor = previous; }

        // Recorded like any other open. An audit trail with the setup tests
        // missing has a hole in it exactly where somebody would look first.
        Audit(probe, error);

        if (error is not null)
        {
            Say(error, MessageBoxIcon.Warning);
            return;
        }

        Say($"{set.Title} — a {probe.EffectiveDrawerPulseMs}ms pulse on unit {probe.EffectiveDrawerUnit} "
          + $"went to {Shorten(queue)}." + Environment.NewLine + Environment.NewLine
          + "That only means the printer accepted the bytes." + Environment.NewLine + Environment.NewLine
          + "The drawer should have opened. If nothing moved, the printer speaks the other command "
          + "set — change it and press this again. If it clicked but stayed shut, its latch needs a "
          + "longer pulse: try 250 or 300.",
            MessageBoxIcon.Information);
    }

    private void Audit(Destination probe, string? error)
    {
        try
        {
            new DrawerAudit(JsonSettingsStore.FolderPath).Write(new DrawerAuditEntry(
                DateTimeOffset.Now,
                _stationName.Text.Trim(),
                probe.Name,
                probe.Queue,
                error is null ? DrawerAudit.Opened : DrawerAudit.Failed,
                CommandSet: probe.DrawerCommand,
                PulseMs: probe.EffectiveDrawerPulseMs,
                Unit: probe.EffectiveDrawerUnit,
                Reason: "test:setup",
                Error: error));
        }
        catch
        {
            // A settings dialog must not fail over a log line.
        }
    }

    private static void Say(string message, MessageBoxIcon icon) =>
        MessageBox.Show(message, "MaplePrint", MessageBoxButtons.OK, icon);

    private static string Shorten(string value) =>
        value.Length <= 40 ? value : value[..37] + "…";

    /// <summary>How a stored destination reads in the Cash drawer column.</summary>
    private static string DrawerLabel(Destination d) =>
        PrinterCommandSets.For(d.DrawerCommand)?.Title ?? NoDrawer;

    /// <summary>The Cash drawer column back into what a destination stores,
    /// or null for "(none)" and anything unrecognised — both of which mean no
    /// drawer.</summary>
    private static string? DrawerCommandFromLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || string.Equals(label, NoDrawer, StringComparison.Ordinal))
            return null;

        return PrinterCommandSets.All
            .FirstOrDefault(c => string.Equals(c.Title, label, StringComparison.Ordinal))?.Name;
    }

    /// <summary>How a stored destination reads in the Kind column.</summary>
    private static string KindLabel(Destination d) =>
        d.Kind != DestinationKind.Raw
            ? KindDocument
            : string.Equals(d.Language, PrinterLanguages.Zpl, StringComparison.OrdinalIgnoreCase)
                ? KindRawZpl
                : KindRawEscPos;

    /// <summary>The Kind column back into what a destination stores. ESC/POS is
    /// the fallback for any raw entry, which is what a blank language already
    /// meant to every caller.</summary>
    private static (DestinationKind Kind, string? Language) KindFromLabel(string label)
    {
        if (string.Equals(label, KindRawZpl, StringComparison.OrdinalIgnoreCase))
            return (DestinationKind.Raw, PrinterLanguages.Zpl);

        if (string.Equals(label, KindRawEscPos, StringComparison.OrdinalIgnoreCase))
            return (DestinationKind.Raw, PrinterLanguages.EscPos);

        return (DestinationKind.Document, null);
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
