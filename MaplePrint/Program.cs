using System.Runtime.Versioning;

namespace MaplePrint;

[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>
    /// Global so it covers every session on the PC. Two agents on one counter
    /// would both answer the same job and print the customer's receipt twice,
    /// which is the kind of bug that only shows up in front of a queue.
    /// </summary>
    private const string GlobalMutexName = @"Global\MaplePrint.SingleInstance";

    /// <summary>
    /// Creating a <c>Global\</c> object needs SeCreateGlobalPrivilege, which a
    /// standard user may not hold on a locked-down PC. Falling back to a
    /// session-local name keeps the common case (one signed-in cashier) covered
    /// instead of failing to start at all.
    /// </summary>
    private const string LocalMutexName = @"Local\MaplePrint.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        // Checked before the single-instance lock: a diagnostic must be usable
        // while the tray copy is running, which is exactly when someone needs it.
        if (args.Length >= 2 && string.Equals(args[0], "--print-test", StringComparison.OrdinalIgnoreCase))
        {
            RunPrintTest(args[1], args.Length >= 3 ? args[2] : null, args.Length >= 4 ? args[3] : null);
            return;
        }

        // Renders the calibration slip to a PNG without printing it — the
        // "show me exactly what you would have sent" half of a support call.
        if (args.Length >= 2 && string.Equals(args[0], "--render-test", StringComparison.OrdinalIgnoreCase))
        {
            RunRenderTest(args[1], args.Length >= 3 && int.TryParse(args[2], out var dots) ? dots : 0);
            return;
        }

        using var mutex = AcquireSingleInstanceLock(out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "MaplePrint is already running — look for the maple leaf in the notification area, "
                + "near the clock.",
                "MaplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // Disposed explicitly. Application.Run does not dispose the context it is
        // handed, so without this Exit ended the message loop and left the HTTP
        // server listening — the tray icon vanished while the process stayed in
        // Task Manager, still answering print jobs.
        using (var context = new TrayApplicationContext())
        {
            Application.Run(context);
        }

        GC.KeepAlive(mutex);

        // Belt and braces. Everything of ours is shut down by here, but Kestrel
        // and the printing stack both bring their own threads, and one of them
        // outliving Main is exactly what "Exit doesn't close it" looked like to
        // the shop. Exit must mean exit on a counter PC — there is nobody there
        // to open Task Manager.
        Environment.Exit(0);
    }

    private static void RunRenderTest(string outputPath, int dotWidth)
    {
        var resultPath = Path.Combine(Path.GetTempPath(), "mapleprint-selftest.txt");
        var lines = new List<string> { $"render: {outputPath}" };

        try
        {
            using var image = dotWidth > 0
                ? Printing.TestDocuments.CalibrationImage(Environment.MachineName, dotWidth)
                : Printing.TestDocuments.CalibrationImage(Environment.MachineName);

            image.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
            lines.Add($"bitmap: {image.Width}x{image.Height} at {image.HorizontalResolution:0}dpi");
            lines.Add("result: OK");
        }
        catch (Exception ex)
        {
            lines.Add($"result: {ex.GetType().Name}: {ex.Message}");
        }

        try { File.WriteAllLines(resultPath, lines); } catch { }
    }

    /// <summary>
    /// Headless self-test: prints the calibration slip to a queue, optionally to
    /// a file instead of paper, and leaves the outcome in
    /// <c>%TEMP%\mapleprint-selftest.txt</c>.
    ///
    /// There is no console behind a WinExe, so the result goes to a file — which
    /// suits the use it was written for: "run this line and send me what it
    /// produced", without anyone having to drive the tray menu.
    /// </summary>
    private static void RunPrintTest(string queue, string? outputFile, string? paperSizeName)
    {
        var resultPath = Path.Combine(Path.GetTempPath(), "mapleprint-selftest.txt");
        var lines = new List<string>
        {
            $"queue: {queue}",
            $"output: {outputFile ?? "(paper)"}",
            $"paper: {paperSizeName ?? "(driver default)"}"
        };

        try
        {
            using var image = Printing.TestDocuments.CalibrationImage(Environment.MachineName);
            lines.Add($"bitmap: {image.Width}x{image.Height} at {image.HorizontalResolution:0}dpi");

            var error = Printing.DocumentPrinter.Print(
                queue, image, "MaplePrint self-test",
                paperSizeName: paperSizeName,
                note: n => lines.Add($"note: {n}"),
                outputFile: outputFile);

            lines.Add(error is null ? "result: OK" : $"result: {error}");
        }
        catch (Exception ex)
        {
            lines.Add($"result: {ex.GetType().Name}: {ex.Message}");
        }

        try { File.WriteAllLines(resultPath, lines); } catch { }
    }

    /// <summary>
    /// Windows raises <see cref="UnauthorizedAccessException"/> both when the
    /// global name is held by another user's session *and* when this account
    /// simply may not create global objects — the two are indistinguishable
    /// here. We treat every failure as the second case and retry session-locally,
    /// because refusing to start on a PC where nothing is running is the worse
    /// mistake: the shop gets no receipts at all, and the cause is invisible.
    /// </summary>
    private static Mutex AcquireSingleInstanceLock(out bool isFirstInstance)
    {
        try
        {
            return new Mutex(initiallyOwned: true, GlobalMutexName, out isFirstInstance);
        }
        catch
        {
            return new Mutex(initiallyOwned: true, LocalMutexName, out isFirstInstance);
        }
    }
}
