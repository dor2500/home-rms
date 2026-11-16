using System.Diagnostics;
using HomeRms.Shared.Protocol;

namespace HomeRms.Agent.Linux.Features;

/// <summary>Collects installed packages, running processes, and hardware specs on Linux.</summary>
public static class LinuxInventoryCollector
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
        // Try dpkg-query (Debian/Ubuntu), then rpm (Fedora/RHEL), then pacman (Arch)
        var lines = RunLines("dpkg-query", "-W -f=${Package}\\t${Version}\\t${Maintainer}\\n");
        if (lines.Count == 0)
            lines = RunLines("rpm", "-qa --queryformat %{NAME}\\t%{VERSION}-%{RELEASE}\\t%{VENDOR}\\n");
        if (lines.Count == 0)
            lines = RunLines("pacman", "-Q");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]) && seen.Add(parts[0]))
            {
                var name = parts[0].Trim();
                var version = parts.Length > 1 ? parts[1].Trim() : "";
                var pub = parts.Length > 2 ? parts[2].Trim() : "Linux Package";
                if (parts.Length == 1 && name.Contains(' '))
                {
                    var sp = name.Split(' ', 2);
                    name = sp[0];
                    version = sp[1];
                }
                yield return new InstalledApp { Name = name, Version = version, Publisher = pub };
            }
        }
    }

    private static IEnumerable<RunningProcess> CollectProcesses()
    {
        foreach (var p in Process.GetProcesses().OrderByDescending(SafeWorkingSet))
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
            if (File.Exists("/proc/cpuinfo"))
            {
                var cpuLine = File.ReadLines("/proc/cpuinfo")
                    .FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase) ||
                                         l.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase));
                if (cpuLine is not null && cpuLine.Contains(':'))
                    hw.Cpu = cpuLine.Split(':', 2)[1].Trim();
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(hw.Cpu)) hw.Cpu = "Linux CPU";

        try
        {
            if (File.Exists("/proc/meminfo"))
            {
                var memLine = File.ReadLines("/proc/meminfo")
                    .FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase));
                if (memLine is not null)
                {
                    var digits = new string(memLine.Where(char.IsDigit).ToArray());
                    if (long.TryParse(digits, out var kb))
                        hw.TotalRamBytes = kb * 1024L;
                }
            }
        }
        catch { }

        try
        {
            var gpuLines = RunLines("lspci", "-mm");
            var vga = gpuLines.FirstOrDefault(l => l.Contains("VGA", StringComparison.OrdinalIgnoreCase) ||
                                                   l.Contains("3D", StringComparison.OrdinalIgnoreCase));
            if (vga is not null)
                hw.Gpu = vga.Replace("\"", "").Trim();
        }
        catch { }
        if (string.IsNullOrWhiteSpace(hw.Gpu)) hw.Gpu = "Linux Display Adapter";

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                if (drive.TotalSize <= 0) continue;
                if (drive.Name.StartsWith("/snap", StringComparison.Ordinal) ||
                    drive.Name.StartsWith("/boot", StringComparison.Ordinal))
                    continue;
                hw.Disks.Add(new DiskInfo
                {
                    Name = drive.Name,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace
                });
            }
            catch { }
        }

        return hw;
    }

    private static List<string> RunLines(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return [];
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch
        {
            return [];
        }
    }
}
