using System.Text.Json;

namespace MaplePrint.Settings;

/// <summary>
/// Loads / saves <see cref="AppSettings"/> as a single JSON file under
/// <c>%APPDATA%\MaplePrint\settings.json</c>. Returns a fresh
/// <see cref="AppSettings"/> when the file is missing or malformed — an
/// unconfigured PC should open the settings dialog, not crash on launch.
/// </summary>
public static class JsonSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string FolderPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = Path.Combine(appData, "MaplePrint");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string FilePath => Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        File.WriteAllText(FilePath, json);
    }
}
