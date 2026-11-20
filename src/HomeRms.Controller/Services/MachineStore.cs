using System.IO;
using System.Text.Json;
using HomeRms.Controller.Models;
using HomeRms.Controller.Themes;
using HomeRms.Shared;

namespace HomeRms.Controller.Services;

/// <summary>Persists the saved machine list to %AppData%\HomeRMS\machines.json.</summary>
public static class MachineStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeRMS", "machines.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<MachineProfile> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize<List<MachineProfile>>(json);
                if (list is { Count: > 0 }) return list;
            }
        }
        catch (Exception ex)
        {
            // Corrupt file: keep a copy before the default seed below overwrites it.
            var backup = AtomicFile.PreserveCorrupt(FilePath);
            LogBus.Publish(LogBus.Activity, "Settings",
                $"machines.json could not be read ({ex.Message}); starting with the default endpoint." +
                (backup is null ? "" : $" The unreadable file was kept as {Path.GetFileName(backup)}."), PaletteTone.Warning);
        }

        var defaults = new List<MachineProfile>
        {
            new()
            {
                Name = $"{Environment.MachineName} (Local)",
                Host = "localhost",
                Port = 7443,
                StatusLabel = "Ready"
            }
        };
        Save(defaults);
        return defaults;
    }

    public static void Save(IEnumerable<MachineProfile> machines)
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(machines.ToList(), Options));
        }
        catch (Exception ex)
        {
            // The in-memory list stays usable; tell the operator their changes are not persisted.
            LogBus.Publish(LogBus.Activity, "Settings", $"Could not save machines.json: {ex.Message}", PaletteTone.Warning);
        }
    }
}
