namespace MaplePrint.Settings;

/// <summary>
/// Where a window was and how big, so it comes back the way it was left.
/// Keyed by form name in <see cref="AppSettings.WindowPlacements"/>.
/// </summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}
