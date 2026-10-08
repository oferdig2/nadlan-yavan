using System.Diagnostics;

namespace Nadlan.Host.Machine;

/// <summary>A service on the server page. <paramref name="Unit"/> is its systemd unit (restart target).</summary>
public sealed record ServiceDescriptor(string Key, string Title, string ProcessName, string Unit, bool IsThisApp = false)
{
    public static readonly IReadOnlyList<ServiceDescriptor> All = new[]
    {
        new ServiceDescriptor("app", "GreekPlot app", "Nadlan.Host", "nadlan", IsThisApp: true),
        new ServiceDescriptor("mysql", "MySQL", "mysqld", "mysqld"),
        new ServiceDescriptor("nginx", "nginx (web front)", "nginx", "nginx"),
    };
}

public sealed record ServiceSample(
    string Key,
    string Title,
    bool Running,
    int ProcessCount,
    long? StartedUtcMs,
    double? CpuPercent,
    double? MemoryMb,
    double? CgroupMemoryMb,
    double? MemoryHighMb,
    double? MemoryMaxMb);

/// <summary>
/// CPU and memory per service, from its processes (all of them: nginx has a master and workers). CPU is the share of the
/// whole machine since the previous call, like top. On Linux the systemd cgroup adds the memory the kernel charges to the
/// unit and its MemoryHigh/MemoryMax limits (the app runs at 350/450 MB). Called from one thread (MachineMonitor).
/// </summary>
public sealed class ServiceMetrics
{
    private readonly Dictionary<string, (long AtMs, double CpuMs, int ProcessCount)> _previous = new();

    public IReadOnlyList<ServiceSample> Read()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return ServiceDescriptor.All.Select(d => Read(d, now)).ToList();
    }

    private ServiceSample Read(ServiceDescriptor d, long nowMs)
    {
        var processes = d.IsThisApp ? new[] { Process.GetCurrentProcess() } : SafeGetProcesses(d.ProcessName);
        try
        {
            double cpuMs = 0, rssMb = 0;
            long? started = null;
            var counted = 0;
            foreach (var p in processes)
            {
                try
                {
                    p.Refresh();
                    cpuMs += p.TotalProcessorTime.TotalMilliseconds;
                    rssMb += p.WorkingSet64 / 1024.0 / 1024;
                    var start = new DateTimeOffset(p.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
                    started = started is null ? start : Math.Min(started.Value, start);
                    counted++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Exited meanwhile, or (Windows, not elevated) a service process we may not inspect.
                }
            }

            double? cpu = null;
            if (counted > 0 && _previous.TryGetValue(d.Key, out var prev) && prev.ProcessCount == counted && nowMs > prev.AtMs && cpuMs >= prev.CpuMs)
            {
                cpu = Math.Clamp((cpuMs - prev.CpuMs) / ((nowMs - prev.AtMs) * Environment.ProcessorCount) * 100.0, 0, 100);
            }

            _previous[d.Key] = (nowMs, cpuMs, counted);
            var cgroup = CgroupDir(d.Unit);
            return new ServiceSample(d.Key, d.Title, processes.Length > 0, processes.Length, started, cpu,
                counted > 0 ? rssMb : null, ReadCgroupMb(cgroup, "memory.current"), ReadCgroupMb(cgroup, "memory.high"), ReadCgroupMb(cgroup, "memory.max"));
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private static Process[] SafeGetProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<Process>();
        }
    }

    private static string? CgroupDir(string unit)
    {
        var dir = $"/sys/fs/cgroup/system.slice/{unit}.service";
        return OperatingSystem.IsLinux() && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>A cgroup v2 memory file in MB; "max" (no limit) and anything unreadable = null.</summary>
    private static double? ReadCgroupMb(string? dir, string file)
    {
        if (dir is null)
        {
            return null;
        }

        try
        {
            return ulong.TryParse(File.ReadAllText(Path.Combine(dir, file)).Trim(), out var bytes) ? bytes / 1024.0 / 1024 : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
