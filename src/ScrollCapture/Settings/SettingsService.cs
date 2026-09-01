using System.IO;
using System.Text.Json;

namespace ScrollCapture.Settings;

public static class SettingsService
{
    public static string SettingsFilePath { get; } = Path.Combine(Utils.AppPaths.DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= SettingsFilePath;
            if (!File.Exists(path))
            {
                return AppSettings.CreateDefaults();
            }

            string json = File.ReadAllText(path);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? AppSettings.CreateDefaults();
        }
        catch
        {
            // Corrupt settings must never prevent the app from starting.
            return AppSettings.CreateDefaults();
        }
    }

    public static bool Save(AppSettings settings, string? path = null)
    {
        try
        {
            path ??= SettingsFilePath;
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
