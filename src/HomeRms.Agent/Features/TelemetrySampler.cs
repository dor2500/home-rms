using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

namespace HomeRms.Agent.Features;

/// <summary>Windows live telemetry: GetSystemTimes CPU deltas, GlobalMemoryStatusEx, DriveInfo, NIC byte counters.</summary>
public sealed partial class TelemetrySampler : ITelemetrySampler
{
    private ulong _idle, _kernel, _user;
    private long _rx, _tx;
    private long _stamp;

    public void Prime()
    {
        ReadCpu(out _idle, out _kernel, out _user);
        (_rx, _tx) = ReadNetBytes();
        _stamp = Stopwatch.GetTimestamp();
    }

    public TelemetrySample Sample()
    {
        var sample = new TelemetrySample();

        ReadCpu(out var idle, out var kernel, out var user);
        var total = (kernel - _kernel) + (user - _user); // kernel time includes idle time
        var idleDelta = idle - _idle;
        sample.CpuPercent = total == 0 ? 0 : Math.Clamp(100.0 * (total - idleDelta) / total, 0, 100);
        (_idle, _kernel, _user) = (idle, kernel, user);

        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            sample.MemTotalBytes = (long)mem.TotalPhys;
            sample.MemUsedBytes = (long)(mem.TotalPhys - mem.AvailPhys);
        }

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                sample.Disks.Add(new DiskInfo { Name = d.Name, TotalBytes = d.TotalSize, FreeBytes = d.TotalFreeSpace });
            }
            catch { }
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_stamp, now).TotalSeconds);
        var (rx, tx) = ReadNetBytes();
        sample.NetRxBytesPerSec = Math.Max(0, rx - _rx) / seconds;
        sample.NetTxBytesPerSec = Math.Max(0, tx - _tx) / seconds;
        (_rx, _tx, _stamp) = (rx, tx, now);

        sample.UptimeSeconds = Environment.TickCount64 / 1000;
        var procs = Process.GetProcesses();
        sample.ProcessCount = procs.Length;
        foreach (var p in procs) p.Dispose();
        return sample;
    }

    private static void ReadCpu(out ulong idle, out ulong kernel, out ulong user)
    {
        if (!GetSystemTimes(out var i, out var k, out var u))
        {
            idle = kernel = user = 0;
            return;
        }
        idle = i.ToUInt64();
        kernel = k.ToUInt64();
        user = u.ToUInt64();
    }

    private static (long Rx, long Tx) ReadNetBytes()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                try
                {
                    var stats = nic.GetIPStatistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
                catch { }
            }
        }
        catch { }
        return (rx, tx);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
