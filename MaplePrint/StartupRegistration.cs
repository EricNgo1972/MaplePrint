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

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }
}
