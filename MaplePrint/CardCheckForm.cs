using System.Runtime.Versioning;
using MaplePrint.Diagnostics;
using MaplePrint.Settings;
using MaplePrint.UI;

namespace MaplePrint;

/// <summary>
/// The window the card reader check runs in: its steps as they happen, and Yes / No when it asks
/// something. Closing it stops the check. What it showed is kept in
/// <c>%TEMP%\mapleprint-cardcheck.txt</c> — the "send me what it said" half of a support call.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CardCheckForm : Form
{
    private static string TranscriptPath => Path.Combine(Path.GetTempPath(), "mapleprint-cardcheck.txt");

    private readonly AppSettings _settings;
    private readonly ListBox _list;
    private readonly Label _question;
    private readonly Button _yes = Footer("Yes");
    private readonly Button _no = Footer("No");
    private readonly Button _close = Footer("Close");
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _transcript = [];
    private TaskCompletionSource<bool>? _answer;

    public CardCheckForm(AppSettings settings)
    {
        _settings = settings;

        Text = "MaplePrint — Card reader check";
        Width = 720;
        Height = 460;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 300);

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10f),
            IntegralHeight = false,
            HorizontalScrollbar = true
        };

        _question = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 44,
            Padding = new Padding(10, 6, 10, 0),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Visible = false
        };

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(8)
        };
        _yes.Visible = _no.Visible = false;
        _yes.Click += (_, _) => Answer(true);
        _no.Click += (_, _) => Answer(false);
        _close.Click += (_, _) => Close();
        bottom.Controls.Add(_close);
        bottom.Controls.Add(_no);
        bottom.Controls.Add(_yes);

        Controls.Add(_list);
        Controls.Add(_question);
        Controls.Add(bottom);

        WindowPlacementManager.Attach(this, settings, nameof(CardCheckForm));
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        bool ok;
        try
        {
            // Off this thread: the reader's calls block, and the window must stay responsive while
            // somebody fetches a card.
            ok = await Task.Run(() => CardCheck.RunAsync(_settings, line => Post(() => Say(line)), AskAsync, _stop.Token));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Say(new(CardCheckKind.Fail, ex.Message));
            ok = false;
        }

        if (IsDisposed) return;
        Say(new(CardCheckKind.Step, ok ? "The reader works." : "The check did not pass."));
        try { File.WriteAllLines(TranscriptPath, _transcript); } catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _stop.Cancel();
        _answer?.TrySetResult(false);
        base.OnFormClosed(e);
    }

    private void Say(CardCheckLine line)
    {
        if (IsDisposed) return;
        var text = line.Kind switch
        {
            CardCheckKind.Step => $"▸ {line.Text}",
            CardCheckKind.Pass => $"   ✓ {line.Text}",
            CardCheckKind.Warn => $"   ! {line.Text}",
            CardCheckKind.Fail => $"   ✗ {line.Text}",
            _ => $"       {line.Text}",
        };
        _transcript.Add(text);
        _list.Items.Add(text);
        _list.TopIndex = Math.Max(0, _list.Items.Count - 1);
    }

    /// <summary>Called from the check's thread; shows the question and waits for a button.</summary>
    private Task<bool> AskAsync(string question)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = Post(() =>
        {
            _answer = answer;
            _question.Text = question;
            _question.Visible = _yes.Visible = _no.Visible = true;
        });
        if (!asked) answer.TrySetResult(false);
        return answer.Task;
    }

    private void Answer(bool yes)
    {
        _question.Visible = _yes.Visible = _no.Visible = false;
        Say(new(CardCheckKind.Note, $"{_question.Text} {(yes ? "Yes" : "No")}"));
        Interlocked.Exchange(ref _answer, null)?.TrySetResult(yes);
    }

    /// <summary>Onto this window's thread; false when the window has already gone.</summary>
    private bool Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        try { BeginInvoke(action); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static Button Footer(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(84, 0),
        Padding = new Padding(10, 4, 10, 4),
        Margin = new Padding(4, 3, 4, 3)
    };
}
