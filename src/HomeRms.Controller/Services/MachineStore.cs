using System.IO;
using System.Text.Json;
using HomeRms.Controller.Models;

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
        catch
        {
            // Fall through to default seed on missing/corrupt file.
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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(machines.ToList(), Options));
        }
        catch
        {
            // Saving is best-effort; the in-memory list stays usable.
        }
    }
}
