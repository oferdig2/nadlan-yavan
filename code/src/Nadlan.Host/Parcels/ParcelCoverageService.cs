using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;

namespace Nadlan.Host.Parcels;

/// <summary>
/// Background worker that keeps the zoomed-out map cheap: all Parcels united into one surface per kind (ParcelKinds, the
/// OT/plot entry: done, partial, to do) and per <see cref="CoverageLevel"/>, served as ready-made
/// GeoJSON. Recomputed when the parcel table changes - nudged right after a create/edit/delete in this app, and checked every minute (other instances, imports).
/// Kept in memory: each instance computes its own in a second or two.
/// </summary>
public sealed class ParcelCoverageService : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3); // an import creates parcels every few seconds
    // Between two computations at least this long, and at least 4x what the last one took: during an import (a parcel every
    // few seconds) the worker would otherwise spend its whole time re-uniting tens of thousands of polygons.
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    private readonly IParcelStore _parcels;
    private readonly ILogger<ParcelCoverageService> _log;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile Snapshot? _current;
    private DateTime _lastComputedUtc = DateTime.MinValue;
    private TimeSpan _lastDuration = TimeSpan.Zero;

    public ParcelCoverageService(IParcelStore parcels, ILogger<ParcelCoverageService> log)
    {
        _parcels = parcels;
        _log = log;
    }

    /// <summary>One computed state. Json holds, per level, { "real": MultiPolygon, "provisional": MultiPolygon }.</summary>
    public sealed record Snapshot(string Version, DateTime ComputedUtc, long ParcelCount, IReadOnlyDictionary<string, string> Json);

    /// <summary>Null until the first computation finished (a second or so after start).</summary>
    public Snapshot? Current => _current;

    /// <summary>Parcels changed: recompute soon.</summary>
    public void MarkDirty()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    public static string VersionOf(ParcelFingerprint f) =>
        string.Create(CultureInfo.InvariantCulture, $"{f.Count}-{f.MaxId}-{f.LastUpdatedUtc?.Ticks ?? 0}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshIfChangedAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Parcel coverage could not be computed; the zoomed-out map keeps the previous one.");
            }

            try
            {
                if (await _wake.WaitAsync(CheckEvery, stoppingToken))
                {
                    await Task.Delay(Debounce, stoppingToken);
                }

                var gap = MinGap > _lastDuration * 4 ? MinGap : _lastDuration * 4;
                var wait = _lastComputedUtc + gap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshIfChangedAsync(CancellationToken ct)
    {
        var fingerprint = await _parcels.GetFingerprintAsync(ct);
        var version = VersionOf(fingerprint);
        if (_current?.Version == version)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        var all = await _parcels.ListAllGeometriesAsync(ct);
        // One surface per ParcelKinds value (its map colour): { "done": MultiPolygon, "partial": ..., "todo": ..., "vertices": n }.
        var json = await Task.Run(() => CoverageLevel.All.ToDictionary(level => level.Name, level =>
        {
            var parts = new List<string>();
            var vertices = 0;
            foreach (var kind in ParcelKinds.All)
            {
                var surface = ParcelCoverageBuilder.Build(all.Where(p => p.Kind == kind).Select(p => p.Geometry).ToList(), level);
                vertices += ParcelCoverageBuilder.VertexCount(surface);
                parts.Add($"\"{kind}\":{MultiPolygonJson(surface)}");
            }

            return $"{{{string.Join(",", parts)},\"vertices\":{vertices}}}";
        }), ct);

        _current = new Snapshot(version, DateTime.UtcNow, fingerprint.Count, json);
        _lastComputedUtc = DateTime.UtcNow;
        _lastDuration = watch.Elapsed;
        _log.LogInformation("Parcel coverage for {Count} parcels computed in {Ms} ms.", fingerprint.Count, watch.ElapsedMilliseconds);
    }

    /// <summary>GeoJSON MultiPolygon, 6 decimals (≈0.1 m) to keep it small.</summary>
    private static string MultiPolygonJson(IReadOnlyList<GeoPolygon> polygons)
    {
        var sb = new StringBuilder("{\"type\":\"MultiPolygon\",\"coordinates\":[");
        for (var i = 0; i < polygons.Count; i++)
        {
            sb.Append(i == 0 ? "[" : ",[");
            var rings = polygons[i].Rings;
            for (var r = 0; r < rings.Count; r++)
            {
                sb.Append(r == 0 ? "[" : ",[");
                for (var k = 0; k < rings[r].Count; k++)
                {
                    var pt = rings[r][k];
                    sb.Append(k == 0 ? "[" : ",[").Append(pt.Lon.ToString("0.######", CultureInfo.InvariantCulture))
                      .Append(',').Append(pt.Lat.ToString("0.######", CultureInfo.InvariantCulture)).Append(']');
                }

                sb.Append(']');
            }

            sb.Append(']');
        }

        return sb.Append("]}").ToString();
    }
}
