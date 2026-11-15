using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using HomeRms.Shared.Protocol;
using Microsoft.Win32;

namespace HomeRms.Agent.Features;

/// <summary>Collects installed apps (registry), running processes, and hardware specs.</summary>
[SupportedOSPlatform("windows")]
public static class InventoryCollector
{
    public static InventorySnapshot Collect()
    {
        var snapshot = new InventorySnapshot
        {
            CollectedAtUtc = DateTime.UtcNow.ToString("O"),
            Hardware = CollectHardware()
        };
        snapshot.Apps.AddRange(CollectApps());
        snapshot.Processes.AddRange(CollectProcesses());
        return snapshot;
    }

    private static IEnumerable<InstalledApp> CollectApps()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new (RegistryHive Hive, RegistryView View, string Path)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, view, path) in roots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(path);
            if (uninstall is null) continue;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                using var app = uninstall.OpenSubKey(subName);
                var name = app?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (app?.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (!seen.Add(name)) continue;

                yield return new InstalledApp
                {
                    Name = name,
                    Version = app?.GetValue("DisplayVersion") as string ?? "",
                    Publisher = app?.GetValue("Publisher") as string ?? "",
                    InstallDate = app?.GetValue("InstallDate") as string ?? ""
                };
            }
        }
    }

    private static IEnumerable<RunningProcess> CollectProcesses()
    {
        foreach (var p in Process.GetProcesses().OrderByDescending(p => SafeWorkingSet(p)))
        {
            long ws;
            string name;
            try { name = p.ProcessName; ws = SafeWorkingSet(p); }
            catch { continue; }
            yield return new RunningProcess { Pid = p.Id, Name = name, WorkingSetBytes = ws };
        }
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    private static HardwareInfo CollectHardware()
    {
        var hw = new HardwareInfo { LogicalCores = Environment.ProcessorCount };

        try
        {
            using var cpu = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            hw.Cpu = cpu.Get().Cast<ManagementObject>().FirstOrDefault()?["Name"]?.ToString()?.Trim() ?? "Unknown CPU";
        }
        catch { hw.Cpu = "Unknown CPU"; }

        try
        {
            using var gpu = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            hw.Gpu = gpu.Get().Cast<ManagementObject>().FirstOrDefault()?["Name"]?.ToString()?.Trim() ?? "Unknown GPU";
        }
        catch { hw.Gpu = "Unknown GPU"; }

        try
        {
            using var cs = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            var mem = cs.Get().Cast<ManagementObject>().FirstOrDefault()?["TotalPhysicalMemory"];
            hw.TotalRamBytes = mem is null ? 0 : Convert.ToInt64(mem);
        }
        catch { hw.TotalRamBytes = 0; }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                hw.Disks.Add(new DiskInfo
                {
                    Name = drive.Name,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace
                });
            }
            catch { /* skip inaccessible drive */ }
        }

        return hw;
    }
}
