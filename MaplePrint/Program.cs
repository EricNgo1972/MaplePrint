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
    private static void Main()
    {
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
        Application.Run(new TrayApplicationContext());

        GC.KeepAlive(mutex);
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
