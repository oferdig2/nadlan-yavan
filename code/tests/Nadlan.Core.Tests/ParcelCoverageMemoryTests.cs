using Microsoft.Extensions.Logging.Abstractions;
using Nadlan.Core.Activity;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Host.Parcels;
using Xunit.Abstractions;

namespace Nadlan.Core.Tests;

/// <summary>Measures memory: runs alone, so other tests' allocations don't count.</summary>
[CollectionDefinition(nameof(CoverageMemory), DisableParallelization = true)]
public sealed class CoverageMemory;

/// <summary>
/// The zoomed-out surface for big areas (Athens: 16,643 Parcels ran the 200 MB heap out of memory, 2026-11-24): tiles,
/// the saved surface served after a restart, and the back-off after a failed build.
/// </summary>
[Collection(nameof(CoverageMemory))]
public class ParcelCoverageMemoryTests(ITestOutputHelper output)
{
    private const double Lat0 = 37.98, Lon0 = 23.73; // Athens
    private static readonly double MetreLon = 1 / (111_320 * Math.Cos(Lat0 * Math.PI / 180));
    private const double MetreLat = 1 / 110_540.0;

    private static GeoPolygon Square(double x, double y, double size)
    {
        GeoPoint P(double px, double py) => new(Lon0 + px * MetreLon, Lat0 + py * MetreLat);
        return new GeoPolygon(new[] { new[] { P(x, y), P(x + size, y), P(x + size, y + size), P(x, y + size), P(x, y) } });
    }

    /// <summary>
    /// side x side Parcels of 20 m in blocks of 10 x 10, with 12 m roads between blocks; kinds by block, so every kind has
    /// blocks everywhere.
    /// </summary>
    private static List<(string Kind, GeoPolygon Geometry)> City(int side)
    {
        var parcels = new List<(string, GeoPolygon)>(side * side);
        for (var row = 0; row < side; row++)
        {
            for (var col = 0; col < side; col++)
            {
                var x = col * 20 + col / 10 * 12;
                var y = row * 20 + row / 10 * 12;
                parcels.Add((ParcelKinds.All[(row / 10 + col / 10) % 3], Square(x, y, 19.8)));
            }
        }

        return parcels;
    }

    private static Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, List<GeoPolygon>>>> BuildOneArea(
        List<(string Kind, GeoPolygon Geometry)> parcels, int tileParcels = ParcelCoverageByArea.TileParcels) =>
        ParcelCoverageByArea.BuildAsync(new List<(int?, long)> { (1, parcels.Count) }, ParcelKinds.All, CoverageLevel.All,
            (_, _) => Task.FromResult<IReadOnlyList<(string, GeoPolygon)>>(parcels), tileParcels: tileParcels);

    [Fact]
    public async Task Forty_thousand_parcels_in_one_area_stay_well_under_the_server_heap()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var parcels = City(200); // 40,000
        long peak = 0;
        var done = false;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                Thread.Sleep(2);
            }
        }) { IsBackground = true };
        sampler.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var surfaces = await BuildOneArea(parcels);

        Volatile.Write(ref done, true);
        sampler.Join();
        peak = Math.Max(peak, GC.GetTotalMemory(false));
        output.WriteLine($"40,000 parcels: {watch.ElapsedMilliseconds} ms, peak managed memory {peak / 1048576.0:0} MB (input included)");
        Assert.True(peak < 150L * 1024 * 1024, $"peak {peak / 1048576.0:0} MB");
        foreach (var level in CoverageLevel.All)
        {
            var vertices = ParcelKinds.All.Sum(k => ParcelCoverageBuilder.VertexCount(surfaces[level.Name][k]));
            output.WriteLine($"{level.Name}: {vertices} vertices");
            Assert.All(ParcelKinds.All, k => Assert.NotEmpty(surfaces[level.Name][k]));
        }
    }

    [Fact]
    public async Task Tiles_give_the_same_outline_as_one_union()
    {
        var parcels = City(100); // 10,000: 5+ tiles of 2,000

        var tiled = await BuildOneArea(parcels);
        var whole = await BuildOneArea(parcels, tileParcels: int.MaxValue);

        foreach (var level in CoverageLevel.All)
        {
            foreach (var kind in ParcelKinds.All)
            {
                var a = Area(tiled[level.Name][kind]);
                var b = Area(whole[level.Name][kind]);
                output.WriteLine($"{level.Name}/{kind}: tiled {a:0} m², whole {b:0} m², {tiled[level.Name][kind].Count} vs {whole[level.Name][kind].Count} polygons");
                Assert.InRange(a / b, 0.98, 1.02);
            }
        }

        // The stitch closes the seams: as many blocks as without tiles (one per block in the mid view, roads kept).
        Assert.Equal(whole["mid"][ParcelKinds.Todo].Count, tiled["mid"][ParcelKinds.Todo].Count);
    }

    [Fact]
    public void A_big_kind_is_split_until_every_tile_is_small_enough()
    {
        var parcels = City(100).Select(p => p.Geometry).ToList();

        var tiles = ParcelCoverageByArea.Tiles(parcels, 1 / MetreLon, 1 / MetreLat, 2_000);

        Assert.True(tiles.Count >= 5);
        Assert.All(tiles, t => Assert.InRange(t.Count, 1, 2_000));
        Assert.Equal(parcels.Count, tiles.Sum(t => t.Count));
        Assert.Single(ParcelCoverageByArea.Tiles(parcels.Take(2_000).ToList(), 1 / MetreLon, 1 / MetreLat, 2_000));
    }

    /// <summary>Square metres (exterior minus holes), in the same local metres the builder uses.</summary>
    private static double Area(IEnumerable<GeoPolygon> polygons)
    {
        static double Ring(IReadOnlyList<GeoPoint> ring)
        {
            double sum = 0;
            for (var i = 0; i < ring.Count - 1; i++)
            {
                sum += ring[i].Lon / MetreLon * (ring[i + 1].Lat / MetreLat) - ring[i + 1].Lon / MetreLon * (ring[i].Lat / MetreLat);
            }

            return Math.Abs(sum / 2);
        }

        return polygons.Sum(p => Ring(p.Rings[0]) - p.Rings.Skip(1).Sum(Ring));
    }

    // ------------------------------------------------------------ the service: saved surface and back-off

    [Fact]
    public async Task After_a_restart_the_saved_surface_is_served_at_once()
    {
        var path = Path.Combine(Path.GetTempPath(), "nadlan-coverage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new Store(City(20));
            var first = new ParcelCoverageService(store, NullLogger<ParcelCoverageService>.Instance, path);
            Assert.True(await first.RunOnceAsync());
            Assert.True(File.Exists(path));

            // A new instance (the app restarted): served before any build.
            var second = new ParcelCoverageService(store, NullLogger<ParcelCoverageService>.Instance, path);
            Assert.Null(second.Current);
            second.LoadSaved();
            Assert.NotNull(second.Current);
            Assert.Equal(first.Current!.Version, second.Current!.Version);
            Assert.Equal(first.Current.Json["overview"], second.Current.Json["overview"]);
            Assert.True(second.Status.FromFile);
            Assert.Equal(400, second.Status.BuiltParcels);

            // Same Parcels: nothing to build. A Parcel more: built again, the old surface served meanwhile.
            Assert.False(await second.RunOnceAsync());
            store.Fingerprint = store.Fingerprint with { Count = 401, MaxId = 401 };
            Assert.True(await second.RunOnceAsync());
            Assert.False(second.Status.FromFile);
            Assert.Equal(401, second.Current.ParcelCount);

            // Linking a beach Asset changes the colours: rebuilt too.
            store.Fingerprint = store.Fingerprint with { BeachParcels = 1, BeachParcelIdSum = 17 };
            Assert.True(await second.RunOnceAsync());

            // The Admin's Rebuild: built again although nothing changed.
            Assert.False(await second.RunOnceAsync());
            second.RequestRebuild();
            Assert.True(await second.RunOnceAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_surface_saved_by_other_rules_is_not_served()
    {
        var path = Path.Combine(Path.GetTempPath(), "nadlan-coverage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new Store(City(10));
            await new ParcelCoverageService(store, NullLogger<ParcelCoverageService>.Instance, path).RunOnceAsync();
            var saved = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, saved.Replace($"\"builder\":{ParcelCoverageBuilder.Version}", $"\"builder\":{ParcelCoverageBuilder.Version - 1}"));

            var restarted = new ParcelCoverageService(store, NullLogger<ParcelCoverageService>.Instance, path);
            restarted.LoadSaved();
            Assert.Null(restarted.Current);

            await File.WriteAllTextAsync(path, "{ not json");
            restarted.LoadSaved();
            Assert.Null(restarted.Current); // a broken file is ignored, never a crash
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_failing_build_backs_off_5_min_30_min_then_2_h_and_never_throws()
    {
        var clock = new Clock();
        var store = new Store(City(10)) { Throw = new OutOfMemoryException() };
        var service = new ParcelCoverageService(store, NullLogger<ParcelCoverageService>.Instance, clock: clock);

        foreach (var wait in new[] { 5, 30, 120, 120 })
        {
            Assert.False(await service.RunOnceAsync());
            var status = service.Status;
            Assert.Equal(clock.Now + TimeSpan.FromMinutes(wait), status.NextTryUtc);
            Assert.Equal(clock.Now, status.FailedUtc);
            Assert.Contains("OutOfMemoryException", status.Failure);
            clock.Now += TimeSpan.FromMinutes(wait);
        }

        Assert.Equal(4, service.Status.Failures);
        Assert.Null(service.Current);

        // Works again: the back-off is over.
        store.Throw = null;
        Assert.True(await service.RunOnceAsync());
        Assert.Equal(0, service.Status.Failures);
        Assert.Null(service.Status.NextTryUtc);
        Assert.Null(service.Status.Failure);
        Assert.NotNull(service.Current);

        // A failure later keeps the surface that is served, and starts again at 5 min.
        store.Throw = new InvalidOperationException("boom");
        store.Fingerprint = store.Fingerprint with { Count = 101 };
        Assert.False(await service.RunOnceAsync());
        Assert.NotNull(service.Current);
        Assert.True(service.Status.Stale);
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(5), service.Status.NextTryUtc);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTime Now { get; set; } = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    /// <summary>One area holding the given Parcels; the fingerprint is set by the test.</summary>
    private sealed class Store(List<(string Kind, GeoPolygon Geometry)> parcels) : IParcelStore
    {
        public ParcelFingerprint Fingerprint { get; set; } = new(parcels.Count, parcels.Count, new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc));
        public Exception? Throw { get; set; }

        public Task<ParcelFingerprint> GetFingerprintAsync(CancellationToken ct = default) => Task.FromResult(Fingerprint);
        public Task<IReadOnlyList<(int? AreaId, long Count)>> CountByGeographicAreaAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(int?, long)>>(new List<(int?, long)> { (1, parcels.Count) });
        public Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>> ListGeometriesInAreaAsync(int? areaId, CancellationToken ct = default) =>
            Throw is { } ex ? throw ex : Task.FromResult<IReadOnlyList<(string, GeoPolygon)>>(parcels);

        public Task<Parcel?> GetAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelDeleteOutcome> DeleteAsync(long parcelId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Parcel?> GetByRegistryIdAsync(int c, string r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsValidGeometryAsync(GeoPolygon p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> InsertAsync(Parcel p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Parcel p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon c, double m, CancellationToken ct = default, long? exclude = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>> ListAllGeometriesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, long>> CountByKindAsync(ParcelQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelNumbersResult> SetNumbersAsync(IReadOnlyList<ParcelNumbersWrite> w, long? u, Func<ParcelNumbers, ParcelNumbers, ActivityEntry> d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GeoPoint>> ListAnchorsAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
