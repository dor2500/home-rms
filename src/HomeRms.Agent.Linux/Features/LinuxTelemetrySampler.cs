using System.Diagnostics;
using System.Globalization;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

namespace HomeRms.Agent.Linux.Features;

/// <summary>Linux live telemetry from /proc/stat, /proc/meminfo, /proc/net/dev, /proc/uptime and DriveInfo.</summary>
public sealed class LinuxTelemetrySampler : ITelemetrySampler
{
    private ulong _cpuIdle, _cpuTotal;
    private long _rx, _tx;
    private long _stamp;

    public void Prime()
    {
        (_cpuIdle, _cpuTotal) = ReadCpu();
        (_rx, _tx) = ReadNetBytes();
        _stamp = Stopwatch.GetTimestamp();
    }

    public TelemetrySample Sample()
    {
        var sample = new TelemetrySample();

        var (idle, total) = ReadCpu();
        var dTotal = total - _cpuTotal;
        var dIdle = idle - _cpuIdle;
        sample.CpuPercent = dTotal == 0 || total < _cpuTotal ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
        (_cpuIdle, _cpuTotal) = (idle, total);

        var (memTotal, memAvailable) = ReadMemInfo();
        sample.MemTotalBytes = memTotal;
        sample.MemUsedBytes = Math.Max(0, memTotal - memAvailable);

        var seen = new HashSet<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                if (!seen.Add($"{d.TotalSize}:{d.DriveFormat}:{d.TotalFreeSpace}")) continue; // bind mounts
                sample.Disks.Add(new DiskInfo { Name = d.Name, TotalBytes = d.TotalSize, FreeBytes = d.AvailableFreeSpace });
            }
            catch { }
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_stamp, now).TotalSeconds);
        var (rx, tx) = ReadNetBytes();
        sample.NetRxBytesPerSec = Math.Max(0, rx - _rx) / seconds;
        sample.NetTxBytesPerSec = Math.Max(0, tx - _tx) / seconds;
        (_rx, _tx, _stamp) = (rx, tx, now);

        sample.UptimeSeconds = ReadUptime();
        sample.ProcessCount = CountProcesses();
        return sample;
    }

    /// <summary>First line of /proc/stat: cpu user nice system idle iowait irq softirq steal ...</summary>
    private static (ulong Idle, ulong Total) ReadCpu()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            if (line is null) return (0, 0);
            var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
                .Select(s => ulong.TryParse(s, out var x) ? x : 0).ToArray();
            if (v.Length < 4) return (0, 0);
            var idle = v[3] + (v.Length > 4 ? v[4] : 0); // idle + iowait
            ulong total = 0;
            foreach (var x in v) total += x;
            return (idle, total);
        }
        catch { return (0, 0); }
    }

    private static (long Total, long Available) ReadMemInfo()
    {
        long total = 0, available = -1, free = 0, buffers = 0, cached = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(':', 2);
                if (parts.Length != 2) continue;
                var kb = long.TryParse(parts[1].Trim().Split(' ')[0], out var k) ? k * 1024 : 0;
                switch (parts[0])
                {
                    case "MemTotal": total = kb; break;
                    case "MemAvailable": available = kb; break;
                    case "MemFree": free = kb; break;
                    case "Buffers": buffers = kb; break;
                    case "Cached": cached = kb; break;
                }
            }
        }
        catch { }
        return (total, available >= 0 ? available : free + buffers + cached);
    }

    /// <summary>/proc/net/dev: "iface: rx_bytes rx_packets ... (8 rx fields) tx_bytes ..."; loopback excluded.</summary>
    private static (long Rx, long Tx) ReadNetBytes()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
            {
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var name = line[..idx].Trim();
                if (name == "lo") continue;
                var f = line[(idx + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 9) continue;
                if (long.TryParse(f[0], out var r)) rx += r;
                if (long.TryParse(f[8], out var t)) tx += t;
            }
        }
        catch { }
        return (rx, tx);
    }

    private static long ReadUptime()
    {
        try
        {
            var first = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? (long)s : 0;
        }
        catch { return Environment.TickCount64 / 1000; }
    }

    private static int CountProcesses()
    {
        try
        {
            return Directory.EnumerateDirectories("/proc")
                .Count(d => Path.GetFileName(d).All(char.IsAsciiDigit));
        }
        catch { return 0; }
    }
}
