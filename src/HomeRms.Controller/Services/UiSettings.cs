using System.IO;
using System.Text.Json;

namespace HomeRms.Controller.Services;

/// <summary>Shell layout preferences, persisted to %AppData%\HomeRMS\ui-settings.json.</summary>
public sealed class UiSettings
{
    public bool ConsoleOpen { get; set; }
    public double ConsoleHeight { get; set; } = 220;
    public string ConsoleFilter { get; set; } = "All";
    public bool SidebarCollapsed { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeRMS", "ui-settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
        }
        catch { /* corrupt or unreadable: fall back to defaults */ }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* preferences are best-effort */ }
    }
}
