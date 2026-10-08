using Nadlan.Persistence.MySql;

namespace Nadlan.Host.Machine;

/// <summary>Everything the server page shows at one moment.</summary>
public sealed record MachineSnapshot(
    long CapturedUtcMs,
    MachineSample Machine,
    IReadOnlyList<ServiceSample> Services,
    MySqlSample MySql,
    AppSample App);

/// <summary>This app from the inside: .NET heap against its limit (DOTNET_GCHeapHardLimit), threads, traffic.</summary>
public sealed record AppSample(
    double GcHeapMb,
    double? GcHeapLimitMb,
    int Threads,
    int Gen2Collections,
    double? RequestsPerMinute,
    double? ServerErrorsPerMinute,
    long RequestsSinceStart,
    long ServerErrorsSinceStart);

/// <summary>
/// Samples the machine, the services and MySQL every 10 seconds and keeps the last hour, so the page shows a trend at once,
/// including what happened before anyone opened it. Each sample reads a few /proc files and runs one SHOW STATUS: a
/// negligible load even on a t3.micro.
/// </summary>
public sealed class MachineMonitor : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private const int HistoryLength = 360; // one hour

    private readonly MachineMetrics _machine;
    private readonly ServiceMetrics _services = new();
    private readonly MySqlStatus _mysql;
    private readonly RequestCounter _requests;
    private readonly ILogger<MachineMonitor> _log;
    private readonly object _gate = new();
    private readonly Queue<MachineSnapshot> _history = new();
    private readonly SemaphoreSlim _sampling = new(1, 1);
    private (long AtMs, long Requests, long Errors)? _previousRequests;
    private MachineSnapshot? _latest;

    public MachineMonitor(IWebHostEnvironment env, MySqlDatabase db, RequestCounter requests, ILogger<MachineMonitor> log)
    {
        _machine = new MachineMetrics(env.ContentRootPath);
        _mysql = new MySqlStatus(db);
        _requests = requests;
        _log = log;
    }

    public MySqlStatus MySql => _mysql;

    /// <summary>The latest sample (taken now if there is none yet) and the history, oldest first.</summary>
    public async Task<(MachineSnapshot Latest, IReadOnlyList<MachineSnapshot> History)> GetAsync(CancellationToken ct)
    {
        if (_latest is null)
        {
            await SampleAsync(ct);
        }

        lock (_gate)
        {
            return (_latest!, _history.ToList());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await SampleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Server metrics sample failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        await _sampling.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var snapshot = new MachineSnapshot(now, _machine.Read(), _services.Read(), await _mysql.ReadAsync(ct), ReadApp(now));
            lock (_gate)
            {
                _latest = snapshot;
                _history.Enqueue(snapshot);
                while (_history.Count > HistoryLength)
                {
                    _history.Dequeue();
                }
            }
        }
        finally
        {
            _sampling.Release();
        }
    }

    private AppSample ReadApp(long nowMs)
    {
        var (requests, errors) = _requests.Totals;
        double? perMinute = null, errorsPerMinute = null;
        if (_previousRequests is { } prev && nowMs > prev.AtMs)
        {
            var minutes = (nowMs - prev.AtMs) / 60000.0;
            perMinute = (requests - prev.Requests) / minutes;
            errorsPerMinute = (errors - prev.Errors) / minutes;
        }

        _previousRequests = (nowMs, requests, errors);
        // With DOTNET_GCHeapHardLimit set (nadlan.service: 200 MB), TotalAvailableMemoryBytes is that limit; without it, it
        // is just the machine's memory, which isn't worth showing as a heap limit.
        double? heapLimit = HasHeapLimit ? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 : null;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return new AppSample(GC.GetTotalMemory(false) / 1024.0 / 1024, heapLimit, self.Threads.Count, GC.CollectionCount(2),
            perMinute, errorsPerMinute, requests, errors);
    }

    private static readonly bool HasHeapLimit = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"));
}

/// <summary>Counts requests and 5xx answers for the server page. First in the pipeline, so errors turned into JSON count too.</summary>
public sealed class RequestCounter
{
    private long _requests;
    private long _errors;

    public (long Requests, long Errors) Totals => (Interlocked.Read(ref _requests), Interlocked.Read(ref _errors));

    public RequestDelegate Wrap(RequestDelegate next) => async context =>
    {
        Interlocked.Increment(ref _requests);
        try
        {
            await next(context);
        }
        catch
        {
            Interlocked.Increment(ref _errors);
            throw;
        }

        if (context.Response.StatusCode >= 500)
        {
            Interlocked.Increment(ref _errors);
        }
    };
}
