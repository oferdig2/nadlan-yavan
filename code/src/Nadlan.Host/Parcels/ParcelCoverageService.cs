using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;

namespace Nadlan.Host.Parcels;

/// <summary>
/// Background worker that keeps the zoomed-out map cheap: all Parcels united into one surface per kind (ParcelKinds, the
/// OT/plot entry: done, partial, to do) and per <see cref="CoverageLevel"/>, served as ready-made
/// GeoJSON. Recomputed when the parcel table changes - nudged right after a create/edit/delete in this app, and checked every minute (other instances, imports).
/// The last good surface is also saved to a file (coverage.json in the service's state directory) and served from it right
/// after a restart, while the check below decides whether it is still current (36,646 Parcels take about 30 s to build).
/// </summary>
public sealed class ParcelCoverageService : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3); // an import creates parcels every few seconds
    // Between two computations at least this long, and at least 4x what the last one took: during an import (a parcel every
    // few seconds) the worker would otherwise spend its whole time re-uniting tens of thousands of polygons.
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits after the 1st, 2nd, 3rd and later failure in a row. On 2026-11-24 a build that ran out of memory was retried
    /// every 80 s for hours (most of the server's CPU); a parcel edit doesn't cut a wait short, the Admin's Rebuild does.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff = new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2) };

    private readonly IParcelStore _parcels;
    private readonly ILogger<ParcelCoverageService> _log;
    private readonly string? _snapshotPath;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile Snapshot? _current;
    private volatile CoverageStatus _status = CoverageStatus.Empty;
    private volatile bool _rebuildRequested;
    private DateTime _lastComputedUtc = DateTime.MinValue;
    private TimeSpan _lastDuration = TimeSpan.Zero;
    private int _failures;
    private DateTime? _retryUtc;
    private bool _fromFile;
    private string? _databaseVersion;
    private long? _databaseParcels;
    private DateTime? _failedUtc;
    private string? _failure;

    /// <param name="snapshotPath">Where the last good surface is saved (null: not saved, as in most tests).</param>
    public ParcelCoverageService(IParcelStore parcels, ILogger<ParcelCoverageService> log, string? snapshotPath = null, TimeProvider? clock = null)
    {
        _parcels = parcels;
        _log = log;
        _snapshotPath = snapshotPath;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The service's StateDirectory (/var/lib/nadlan on the server) or App_Data on a developer PC.</summary>
    public static string DefaultSnapshotPath(string contentRoot)
    {
        var state = (Environment.GetEnvironmentVariable("STATE_DIRECTORY") ?? "").Split(':')[0];
        return Path.Combine(string.IsNullOrWhiteSpace(state) ? Path.Combine(contentRoot, "App_Data") : state, "coverage.json");
    }

    /// <summary>One computed state. Json holds, per level, { "done": MultiPolygon, "partial": ..., "todo": ..., "vertices": n }.</summary>
    public sealed record Snapshot(string Version, DateTime ComputedUtc, long ParcelCount, IReadOnlyDictionary<string, string> Json, double BuildSeconds = 0);

    /// <summary>For Admin > Server: what is served, whether the database has moved on, and the last failure.</summary>
    public sealed record CoverageStatus(DateTime? BuiltUtc, long? BuiltParcels, double? BuildSeconds, bool FromFile,
        long? DatabaseParcels, bool Stale, bool Building, int Failures, DateTime? FailedUtc, string? Failure, DateTime? NextTryUtc)
    {
        public static readonly CoverageStatus Empty = new(null, null, null, false, null, false, false, 0, null, null, null);
    }

    /// <summary>Null until the first computation finished, or a saved one was loaded at start.</summary>
    public Snapshot? Current => _current;

    public CoverageStatus Status => _status;

    /// <summary>Parcels changed: recompute soon.</summary>
    public void MarkDirty()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <summary>Admin's "Rebuild map overview": now, whatever the fingerprint or a back-off says. The old surface is served meanwhile.</summary>
    public void RequestRebuild()
    {
        _rebuildRequested = true;
        MarkDirty();
    }

    public static string VersionOf(ParcelFingerprint f) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{ParcelCoverageBuilder.Version}-{f.Count}-{f.MaxId}-{f.LastUpdatedUtc?.Ticks ?? 0}-{f.BeachParcels}-{f.BeachParcelIdSum}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before the first build: the map paints right after a restart, from the last good surface.
        LoadSaved();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                await WaitForNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task WaitForNextAsync(CancellationToken ct)
    {
        if (_retryUtc is { } retry)
        {
            await WaitUntilAsync(retry, ct); // backing off after a failure
            return;
        }

        if (!_rebuildRequested && await _wake.WaitAsync(CheckEvery, ct))
        {
            await Task.Delay(Debounce, _clock, ct);
        }

        var gap = MinGap > _lastDuration * 4 ? MinGap : _lastDuration * 4;
        await WaitUntilAsync(_lastComputedUtc + gap, ct);
    }

    /// <summary>Until then, or until the Admin asks for a rebuild: a parcel edit meanwhile is seen by the next check.</summary>
    private async Task WaitUntilAsync(DateTime untilUtc, CancellationToken ct)
    {
        while (!_rebuildRequested)
        {
            var left = untilUtc - _clock.GetUtcNow().UtcDateTime;
            if (left <= TimeSpan.Zero)
            {
                return;
            }

            await _wake.WaitAsync(left, ct);
        }
    }

    /// <summary>
    /// One check, and a build when the Parcels changed (or a Rebuild was asked for). Never throws but on cancellation:
    /// a failure - out of memory included - is recorded, backed off and logged, and the app keeps running.
    /// </summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        var forced = _rebuildRequested;
        _rebuildRequested = false;
        try
        {
            var fingerprint = await _parcels.GetFingerprintAsync(ct);
            var version = VersionOf(fingerprint);
            _databaseVersion = version;
            _databaseParcels = fingerprint.Count;
            if (!forced && _current?.Version == version)
            {
                // Current (a failed build of a state that has since gone back doesn't matter any more).
                _failures = 0;
                _retryUtc = null;
                return false;
            }

            Publish(building: true);
            await BuildAsync(version, fingerprint.Count, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is OutOfMemoryException)
            {
                // The half-built geometries are garbage now; give the memory back before the requests need it.
                GC.Collect();
            }

            Fail(ex);
            return false;
        }
        finally
        {
            Publish(building: false);
        }
    }

    private async Task BuildAsync(string version, long parcelCount, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        // One geographic area at a time (only its Parcels in memory), big areas in tiles, on a worker thread: uniting all
        // of Greece at once ran the server out of its 200 MB heap at 13,952 Parcels (2026-10-21), Athens alone at 16,643 (2026-11-24).
        var areas = await _parcels.CountByGeographicAreaAsync(ct);
        var surfaces = await Task.Run(() => ParcelCoverageByArea.BuildAsync(areas, ParcelKinds.All, CoverageLevel.All,
            (areaId, token) => _parcels.ListGeometriesInAreaAsync(areaId, token), ct), ct);

        // One surface per ParcelKinds value (its map colour): { "done": MultiPolygon, "partial": ..., "todo": ..., "vertices": n }.
        var json = CoverageLevel.All.ToDictionary(level => level.Name, level =>
        {
            var parts = new List<string>();
            var vertices = 0;
            foreach (var kind in ParcelKinds.All)
            {
                var surface = surfaces[level.Name][kind];
                vertices += ParcelCoverageBuilder.VertexCount(surface);
                parts.Add($"\"{kind}\":{MultiPolygonJson(surface)}");
            }

            return $"{{{string.Join(",", parts)},\"vertices\":{vertices}}}";
        });

        var now = _clock.GetUtcNow().UtcDateTime;
        var snapshot = new Snapshot(version, now, parcelCount, json, Math.Round(watch.Elapsed.TotalSeconds, 1));
        _current = snapshot;
        _fromFile = false;
        _lastComputedUtc = now;
        _lastDuration = watch.Elapsed;
        _failures = 0;
        _retryUtc = null;
        _failedUtc = null;
        _failure = null;
        _log.LogInformation("Parcel coverage for {Count} parcels computed in {Ms} ms.", parcelCount, watch.ElapsedMilliseconds);
        Save(snapshot);
    }

    private void Fail(Exception ex)
    {
        _failures++;
        var now = _clock.GetUtcNow().UtcDateTime;
        var wait = Backoff[Math.Min(_failures, Backoff.Count) - 1];
        _failedUtc = now;
        _failure = ex.GetType().Name + ": " + ex.Message;
        _retryUtc = now + wait;
        // The whole exception once per back-off step; at the last step one line each time (every 2 h).
        if (_failures <= Backoff.Count)
        {
            _log.LogWarning(ex, "Parcel coverage could not be computed (failure {Failures} in a row); the zoomed-out map keeps the previous one. Next try in {Wait}.",
                _failures, wait);
        }
        else
        {
            _log.LogWarning("Parcel coverage could not be computed again (failure {Failures} in a row): {Failure}. Next try in {Wait}.", _failures, _failure, wait);
        }
    }

    private void Publish(bool building)
    {
        var current = _current;
        _status = new CoverageStatus(current?.ComputedUtc, current?.ParcelCount, current?.BuildSeconds, _fromFile,
            _databaseParcels, current is not null && _databaseVersion is not null && current.Version != _databaseVersion,
            building, _failures, _failedUtc, _failure, _retryUtc);
    }

    // ------------------------------------------------------------ the saved surface

    /// <summary>
    /// Writes the snapshot next to the old one, then swaps (a crash mid-write leaves the old file). A failure is logged
    /// only: the surface in memory is fine.
    /// </summary>
    private void Save(Snapshot snapshot)
    {
        if (_snapshotPath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_snapshotPath))!);
            var temp = _snapshotPath + ".tmp";
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("builder", ParcelCoverageBuilder.Version);
                writer.WriteString("version", snapshot.Version);
                writer.WriteString("computedUtc", snapshot.ComputedUtc);
                writer.WriteNumber("parcelCount", snapshot.ParcelCount);
                writer.WriteNumber("buildSeconds", snapshot.BuildSeconds);
                writer.WriteStartObject("levels");
                foreach (var (level, json) in snapshot.Json)
                {
                    writer.WritePropertyName(level);
                    writer.WriteRawValue(json, skipInputValidation: true);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            File.Move(temp, _snapshotPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "The map overview could not be saved to {Path}; after a restart it is built again.", _snapshotPath);
        }
    }

    /// <summary>
    /// The saved surface, if it was made by today's rules (<see cref="ParcelCoverageBuilder.Version"/>) and has every level;
    /// otherwise nothing is served until the first build. Whether it is still current is the first check's job.
    /// </summary>
    internal void LoadSaved()
    {
        if (_snapshotPath is null || !File.Exists(_snapshotPath))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_snapshotPath));
            var root = doc.RootElement;
            if (root.GetProperty("builder").GetInt32() != ParcelCoverageBuilder.Version)
            {
                _log.LogInformation("The saved map overview was made by other rules (builder {Builder}); building a new one.", root.GetProperty("builder").GetInt32());
                return;
            }

            var levels = root.GetProperty("levels");
            var json = new Dictionary<string, string>();
            foreach (var level in CoverageLevel.All)
            {
                if (!levels.TryGetProperty(level.Name, out var surface))
                {
                    return;
                }

                json[level.Name] = surface.GetRawText();
            }

            _current = new Snapshot(root.GetProperty("version").GetString()!, root.GetProperty("computedUtc").GetDateTime().ToUniversalTime(),
                root.GetProperty("parcelCount").GetInt64(), json, root.GetProperty("buildSeconds").GetDouble());
            _fromFile = true;
            _log.LogInformation("Map overview of {Count} parcels loaded from {Path} (built {Utc:u}).", _current.ParcelCount, _snapshotPath, _current.ComputedUtc);
            Publish(building: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            _log.LogWarning(ex, "The saved map overview {Path} could not be read; building a new one.", _snapshotPath);
        }
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
