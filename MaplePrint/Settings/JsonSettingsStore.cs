using System.Text.Json;
using System.Text.Json.Serialization;

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
        // Kinds read as "Document"/"Raw" rather than 0/1, so the file stays
        // legible to whoever opens it during a support call.
        Converters = { new JsonStringEnumConverter() }
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
        AppSettings settings;
        var unreadable = false;

        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            // Present but not parseable.
            settings = new AppSettings();
            unreadable = true;
        }

        var migrated = settings.Migrate();

        // Persist so the legacy keys disappear, and so a fresh install has a
        // settings file someone can actually look at. The one case we skip is a
        // file that exists but wouldn't parse — rewriting that would destroy a
        // hand-edit whose only problem is a stray comma.
        if (migrated && !unreadable)
        {
            try { Save(settings); } catch { }
        }

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        File.WriteAllText(FilePath, json);
    }
}
