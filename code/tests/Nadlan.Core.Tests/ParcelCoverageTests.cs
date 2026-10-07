using Nadlan.Core.Activity;
using Nadlan.Core.Assets;
using Nadlan.Core.Files;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

/// <summary>The zoomed-out surface (union + closing + simplify) and Parcel deletion.</summary>
public class ParcelCoverageTests
{
    // Metres around Skroponeria (lat 38.6) to degrees.
    private const double Lat0 = 38.6, Lon0 = 23.3;
    private static readonly double MetreLon = 1 / (111_320 * Math.Cos(Lat0 * Math.PI / 180));
    private const double MetreLat = 1 / 110_540.0;

    private static GeoPolygon Square(double xMetres, double yMetres, double size = 50)
    {
        GeoPoint P(double x, double y) => new(Lon0 + x * MetreLon, Lat0 + y * MetreLat);
        var ring = new[] { P(xMetres, yMetres), P(xMetres + size, yMetres), P(xMetres + size, yMetres + size), P(xMetres, yMetres + size), P(xMetres, yMetres) };
        return new GeoPolygon(new[] { ring });
    }

    [Theory]
    [InlineData(false, "47", "22", ParcelKinds.Done)]   // green: OT and plot entered, real KAEK or not
    [InlineData(true, "47", "22", ParcelKinds.Done)]
    [InlineData(true, "47", null, ParcelKinds.Partial)]  // only one of them
    [InlineData(false, " ", "22", ParcelKinds.Partial)]
    [InlineData(false, null, null, ParcelKinds.Todo)]    // blue: still to be entered, even with a real KAEK
    [InlineData(true, "  ", "", ParcelKinds.Todo)]
    [InlineData(false, "-", " / ", ParcelKinds.Todo)]    // placeholder dashes are no numbers
    public void A_parcels_map_colour_follows_its_OT_plot_entry(bool provisional, string? ot, string? plot, string kind)
    {
        Assert.Equal(kind, new Parcel { RegistryIdIsProvisional = provisional, OT = ot, PlotNumber = plot, Geometry = Square(0, 0) }.Kind);
    }

    [Fact]
    public void Neighbours_with_a_digitising_sliver_become_one_block()
    {
        var parcels = new[] { Square(0, 0), Square(51, 0), Square(0, 50.5) }; // 1 m and 0.5 m gaps

        var surface = ParcelCoverageBuilder.Build(parcels, CoverageLevel.Mid);

        Assert.Single(surface);
    }

    [Fact]
    public void A_road_keeps_blocks_apart_in_the_mid_view_but_not_in_the_overview()
    {
        var parcels = new[] { Square(0, 0), Square(90, 0) }; // 40 m apart

        Assert.Equal(2, ParcelCoverageBuilder.Build(parcels, CoverageLevel.Mid).Count);
        Assert.Single(ParcelCoverageBuilder.Build(parcels, CoverageLevel.Overview));
    }

    [Fact]
    public void Surface_has_far_fewer_corners_than_the_parcels()
    {
        var parcels = Enumerable.Range(0, 20).SelectMany(i => Enumerable.Range(0, 20).Select(j => Square(i * 50, j * 50))).ToList();

        var surface = ParcelCoverageBuilder.Build(parcels, CoverageLevel.Mid);

        Assert.Single(surface);
        Assert.True(ParcelCoverageBuilder.VertexCount(surface) < 20, $"{ParcelCoverageBuilder.VertexCount(surface)} corners");
        Assert.True(ParcelCoverageBuilder.VertexCount(parcels) >= 2000);
    }

    [Fact]
    public void Over_budget_the_surface_merges_harder_until_it_fits()
    {
        var parcels = Enumerable.Range(0, 4).Select(i => Square(i * 80, 0)).ToList(); // 4 blocks, 30 m apart: 20 corners

        var loose = ParcelCoverageBuilder.Build(parcels, new CoverageLevel("t", 2, 2, 1000));
        var tight = ParcelCoverageBuilder.Build(parcels, new CoverageLevel("t", 2, 2, 12));

        Assert.Equal(4, loose.Count);
        Assert.Single(tight);
        Assert.True(ParcelCoverageBuilder.VertexCount(tight) <= 12);
    }

    [Fact]
    public void Start_view_ignores_a_parcel_drawn_far_away_by_mistake()
    {
        var points = Enumerable.Range(0, 100).Select(i => new GeoPoint(23.28 + i * 1e-4, 38.62 + i * 1e-4)).ToList();
        points.Add(new GeoPoint(21.0, 36.0)); // one stray

        var box = ParcelExtent.Of(points)!.Value;

        Assert.True(box.West > 23 && box.South > 38, $"{box.West},{box.South}");
        Assert.Null(ParcelExtent.Of(Array.Empty<GeoPoint>()));
        Assert.Equal(new GeoBounds(23.3, 38.6, 23.3, 38.6), ParcelExtent.Of(new[] { new GeoPoint(23.3, 38.6) }));
    }

    [Fact]
    public void A_self_crossing_legacy_polygon_does_not_break_the_union()
    {
        GeoPoint P(double x, double y) => new(Lon0 + x * MetreLon, Lat0 + y * MetreLat);
        var bowtie = new GeoPolygon(new[] { new[] { P(200, 0), P(250, 50), P(250, 0), P(200, 50), P(200, 0) } });

        var surface = ParcelCoverageBuilder.Build(new[] { Square(0, 0), bowtie }, CoverageLevel.Mid);

        Assert.NotEmpty(surface);
        Assert.Empty(ParcelCoverageBuilder.Build(Array.Empty<GeoPolygon>(), CoverageLevel.Mid));
    }

    [Fact]
    public async Task A_parcel_an_asset_stands_on_is_not_deleted()
    {
        var parcels = new Parcels();
        var service = new ParcelDeletionService(parcels, new AssetsOn(7), new NoFiles(), new FileService(new NoFiles(), null!, null!, new FileStorageSettings()));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(1));

        Assert.Equal("PARCEL_HAS_ASSETS", ex.Code);
        Assert.Contains("#7", ex.Message);
        Assert.False(parcels.Deleted);
    }

    [Fact]
    public async Task Deleting_a_free_parcel_keeps_its_polygon_in_the_history()
    {
        var parcels = new Parcels();
        var log = new Log();
        var service = new ParcelDeletionService(parcels, new AssetsOn(), new NoFiles(), new FileService(new NoFiles(), null!, null!, new FileStorageSettings()), log);

        var result = await service.DeleteAsync(1);

        Assert.True(parcels.Deleted);
        Assert.Equal("TMP-SKR-OT1-P1", result.RegistryId);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(ActivityActions.ParcelDeleted, entry.ActionType);
        Assert.Contains("POLYGON", System.Text.Json.JsonSerializer.Serialize(entry.Metadata));
    }

    private sealed class Parcels : IParcelStore
    {
        public bool Deleted { get; private set; }
        public Task<Parcel?> GetAsync(long id, CancellationToken ct = default) =>
            Task.FromResult<Parcel?>(Deleted ? null : new Parcel { ParcelId = 1, RegistryId = "TMP-SKR-OT1-P1", RegistryIdIsProvisional = true, Geometry = Square(0, 0) });
        public Task<ParcelDeleteOutcome> DeleteAsync(long parcelId, CancellationToken ct = default) { Deleted = true; return Task.FromResult(ParcelDeleteOutcome.Deleted); }
        public Task<Parcel?> GetByRegistryIdAsync(int c, string r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsValidGeometryAsync(GeoPolygon p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> InsertAsync(Parcel p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Parcel p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon c, double m, CancellationToken ct = default, long? exclude = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelFingerprint> GetFingerprintAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>> ListAllGeometriesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, long>> CountByKindAsync(ParcelQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelNumbersResult> SetNumbersAsync(IReadOnlyList<ParcelNumbersWrite> w, long? u, Func<ParcelNumbers, ParcelNumbers, ActivityEntry> d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GeoPoint>> ListAnchorsAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AssetsOn(params long[] assetIds) : IAssetStore
    {
        public Task<IReadOnlyList<AssetMapItem>> ListByParcelAsync(long parcelId, AccessScope scope, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AssetMapItem>>(assetIds.Select(id => new AssetMapItem { AssetId = id, ParcelId = parcelId, Geometry = Square(0, 0) }).ToList());
        public Task<Asset?> GetAsync(long assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> InsertAsync(Asset asset, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Asset asset, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetMapItem>> QueryAsync(AssetQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetPortfolioMembership>> ListPortfoliosAsync(long assetId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoFiles : IFileAttachmentStore
    {
        public Task<IReadOnlyList<FileListItem>> ListReadyAsync(string t, long id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FileListItem>>(Array.Empty<FileListItem>());
        public Task<long> InsertPendingAsync(FileAttachment f, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileAttachment?> GetAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> MarkReadyAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateMetadataAsync(long id, int t, string? c, string? n, int? s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeletePendingAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileAttachment>> ListStalePendingAsync(DateTime before, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileType>> ListTypesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Log : IActivityLog
    {
        public List<ActivityEntry> Entries { get; } = new();
        public Task RecordAsync(ActivityEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<ActivityItem>> ListAsync(string t, long id, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ActivityItem>> ListByUserAsync(long u, string? t, DateTime? f, DateTime? to, int l, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
