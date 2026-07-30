using System.Runtime.Versioning;
using MaplePrint.Settings;

namespace MaplePrint.UI;

/// <summary>
/// Remembers a window's size and position across runs.
///
/// Ported from MapleScan's manager of the same name so the two desktop apps
/// behave identically — including the part that matters most, which is
/// refusing to restore a window onto a screen that is no longer there.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowPlacementManager
{
    public static void Attach(Form form, AppSettings settings, string key, Action<AppSettings>? save = null)
    {
        Restore(form, settings, key);

        form.FormClosing += (_, _) =>
        {
            Capture(form, settings, key);
            save?.Invoke(settings);
        };
    }

    private static void Restore(Form form, AppSettings settings, string key)
    {
        if (!settings.WindowPlacements.TryGetValue(key, out var placement)) return;
        if (placement.Width <= 0 || placement.Height <= 0) return;

        var bounds = new Rectangle(placement.X, placement.Y, placement.Width, placement.Height);

        // A position saved on a monitor that has since been unplugged would put
        // the window somewhere the user cannot reach it.
        if (!IntersectsAnyScreen(bounds)) return;

        form.StartPosition = FormStartPosition.Manual;
        form.Location = bounds.Location;
        form.Size = bounds.Size;

        if (placement.Maximized) form.WindowState = FormWindowState.Maximized;
    }

    private static void Capture(Form form, AppSettings settings, string key)
    {
        // RestoreBounds, not Bounds, when maximized — otherwise un-maximizing
        // next time would restore to the size of the whole screen.
        var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        settings.WindowPlacements[key] = new WindowPlacement
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = form.WindowState == FormWindowState.Maximized
        };
    }

    private static bool IntersectsAnyScreen(Rectangle bounds) =>
        Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));
}
