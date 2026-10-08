using System.Runtime.InteropServices;

namespace Nadlan.Host.Machine;

/// <summary>Whole-machine numbers at one moment. Null = not available on this OS.</summary>
public sealed record MachineSample(
    double? CpuPercent,
    double? StealPercent,
    double? IoWaitPercent,
    double? MemTotalMb,
    double? MemAvailableMb,
    double? SwapTotalMb,
    double? SwapFreeMb,
    double? DiskTotalGb,
    double? DiskFreeGb,
    double[]? LoadAverage,
    double? UptimeSeconds);

/// <summary>
/// Reads the machine's CPU, memory, swap, disk and load: /proc on the Linux server, Win32 on a developer PC.
/// CPU is a percentage since the previous call, so one instance is kept and called from one thread (MachineMonitor).
/// "Steal" matters on a t3 instance: once its CPU credits are spent, AWS throttles it and steal climbs.
/// </summary>
public sealed class MachineMetrics
{
    private readonly string _diskPath;
    private CpuTicks? _previous;

    public MachineMetrics(string diskPath)
    {
        _diskPath = diskPath;
    }

    public MachineSample Read()
    {
        var cpu = ReadCpuTicks();
        double? cpuPercent = null, steal = null, ioWait = null;
        if (cpu is not null && _previous is { } prev && cpu.Total > prev.Total)
        {
            double total = cpu.Total - prev.Total;
            cpuPercent = Percent((cpu.Total - cpu.Idle - cpu.IoWait) - (prev.Total - prev.Idle - prev.IoWait), total);
            steal = OperatingSystem.IsLinux() ? Percent(cpu.Steal - prev.Steal, total) : null;
            ioWait = OperatingSystem.IsLinux() ? Percent(cpu.IoWait - prev.IoWait, total) : null;
        }

        _previous = cpu ?? _previous;
        var (memTotal, memAvailable, swapTotal, swapFree) = ReadMemory();
        var (diskTotal, diskFree) = ReadDisk();
        return new MachineSample(cpuPercent, steal, ioWait, memTotal, memAvailable, swapTotal, swapFree, diskTotal, diskFree,
            ReadLoadAverage(), ReadUptime());
    }

    private static double Percent(double part, double total) => Math.Clamp(part * 100.0 / total, 0, 100);

    private sealed record CpuTicks(ulong Total, ulong Idle, ulong IoWait, ulong Steal);

    private static CpuTicks? ReadCpuTicks()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Kernel time includes idle time.
                return GetSystemTimes(out var idle, out var kernel, out var user)
                    ? new CpuTicks(kernel.Value + user.Value, idle.Value, 0, 0)
                    : null;
            }

            // cpu  user nice system idle iowait irq softirq steal guest guest_nice (guest is already in user)
            var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            var v = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToArray();
            if (v is null || v.Length < 5)
            {
                return null;
            }

            ulong At(int i) => i < v.Length ? v[i] : 0;
            var total = At(0) + At(1) + At(2) + At(3) + At(4) + At(5) + At(6) + At(7);
            return new CpuTicks(total, At(3), At(4), At(7));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static (double? Total, double? Available, double? SwapTotal, double? SwapFree) ReadMemory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) ? (Mb(status.TotalPhys), Mb(status.AvailPhys), null, null) : (null, null, null, null);
            }

            var kb = File.ReadLines("/proc/meminfo")
                .Select(l => l.Split(':', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => ulong.TryParse(p[1].Trim().Split(' ')[0], out var n) ? n : 0UL);
            double? Get(string key) => kb.TryGetValue(key, out var n) ? n / 1024.0 : null;
            return (Get("MemTotal"), Get("MemAvailable"), Get("SwapTotal"), Get("SwapFree"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null, null, null);
        }
    }

    private (double? Total, double? Free) ReadDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_diskPath)) ?? "/");
            return (drive.TotalSize / 1024.0 / 1024 / 1024, drive.AvailableFreeSpace / 1024.0 / 1024 / 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static double[]? ReadLoadAverage()
    {
        try
        {
            return OperatingSystem.IsLinux()
                ? File.ReadAllText("/proc/loadavg").Split(' ').Take(3).Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static double? ReadUptime()
    {
        try
        {
            return OperatingSystem.IsLinux()
                ? double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)
                : Environment.TickCount64 / 1000.0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static double Mb(ulong bytes) => bytes / 1024.0 / 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
