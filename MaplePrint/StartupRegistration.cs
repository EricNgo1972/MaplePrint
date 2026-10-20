using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MaplePrint;

/// <summary>
/// Registers (or clears) a per-user "run at sign-in" entry.
///
/// HKCU rather than a Windows service, deliberately: the agent prints through
/// the spooler on behalf of a signed-in user, and a counter PC is signed in all
/// day anyway. A service would run in session 0, where per-user printer
/// connections may not exist.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MaplePrint";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // A locked-down PC may deny this; it's a convenience, not a
            // requirement, so never let it stop the settings dialog closing.
        }
    }

    public static bool IsEnabled() => RegisteredCommand() is not null;

    /// <summary>The command line currently registered, or null if there is none.</summary>
    public static string? RegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Make the registry agree with the setting at every start, rather than only
    /// when someone opens the Settings dialog.
    ///
    /// <para>Two ordinary things break an entry written once. MaplePrint ships as
    /// a single .exe that people copy into place, so an upgrade often lands in a
    /// different folder and leaves the old path registered; and cleanup tools
    /// prune <c>Run</c> keys. Either way the shop discovers it the morning after
    /// a reboot, when nothing prints and the one thing nobody suspects is the
    /// thing that did not start.</para>
    ///
    /// <para>Self-heals in the enabled direction only. An entry someone added by
    /// hand is theirs, and removing it because our settings file happens to say
    /// false would be us overruling them.</para>
    /// </summary>
    /// <returns>True if the entry was written or corrected.</returns>
    public static bool Reconcile(bool enabled)
    {
        if (!enabled) return false;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        if (string.Equals(RegisteredCommand(), $"\"{exe}\"", StringComparison.OrdinalIgnoreCase))
            return false;

        Apply(true);
        return true;
    }
}
