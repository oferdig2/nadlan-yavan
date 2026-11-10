using Nadlan.Core.Analytics;

namespace Nadlan.Host.Analytics;

/// <summary>
/// The analytics module's background job (pattern: WorkSweeper): shortly after startup and then daily, gives a place to
/// visits that have an IP but none yet - recorded before the GeoLite2 files were installed, or while one was being
/// replaced. Nothing is deleted (owner: keep everything for now).
/// </summary>
public sealed class GeoBackfill : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private const int Page = 1000;

    private readonly IEventStore _store;
    private readonly IGeoLookup _geo;
    private readonly ILogger<GeoBackfill> _log;

    public GeoBackfill(IEventStore store, IGeoLookup geo, ILogger<GeoBackfill> log)
    {
        _store = store;
        _geo = geo;
        _log = log;
    }

    /// <summary>One pass over every visit without a place; returns how many got one.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var filled = 0;
        long after = 0;
        while (true)
        {
            var page = await _store.ListWithoutGeoAsync(after, Page, ct);
            foreach (var (id, ip) in page)
            {
                if (_geo.Lookup(ip) is { } geo)
                {
                    await _store.SetGeoAsync(id, geo, ct);
                    filled++;
                }
            }

            if (page.Count < Page)
            {
                return filled;
            }

            after = page[^1].ContextId;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    var filled = await RunOnceAsync(stoppingToken);
                    if (filled > 0)
                    {
                        _log.LogInformation("Analytics: placed {Count} visit(s) by their IP.", filled);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // DB hiccup: log and try again tomorrow; never take the app down.
                    _log.LogWarning(ex, "Analytics geo backfill failed; will retry in {Interval}.", Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
