using System.IO;
using System.Text.Json;
using HomeRms.Shared;

namespace HomeRms.Controller.Services;

/// <summary>Shell layout preferences, persisted to %AppData%\HomeRMS\ui-settings.json.</summary>
public sealed class UiSettings
{
    public bool ConsoleOpen { get; set; }
    public double ConsoleHeight { get; set; } = 220;
    public string ConsoleFilter { get; set; } = "All";
    public bool SidebarCollapsed { get; set; }
    /// <summary>Active UI theme: "Dark" (default) or "Light".</summary>
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "en";
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    /// <summary>Remote-desktop quality preset chosen per endpoint (key: StreamPresets.EndpointKey, value: StreamPreset name).</summary>
    public Dictionary<string, string> StreamPresetByEndpoint { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeRMS", "ui-settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable: fall back to defaults (the file is rewritten on the next save).
            LogBus.Publish(LogBus.Activity, "Settings", $"ui-settings.json could not be read ({ex.Message}); using default layout.");
        }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // Preferences are best-effort, but say so instead of failing silently.
            LogBus.Publish(LogBus.Activity, "Settings", $"Could not save ui-settings.json: {ex.Message}");
        }
    }
}
