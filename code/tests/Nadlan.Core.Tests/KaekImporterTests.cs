using Nadlan.KaekImporter.Geometry;
using Nadlan.KaekImporter.Import;
using Nadlan.KaekImporter.Ktimanet;

namespace Nadlan.Core.Tests;

public class KaekImporterTests
{
    // Real reply from gis.ktimanet.gr for a click inside 120981108035 (captured 2026-09-27).
    private const string ParcelReply =
        "GETPSTKG|444875.73687242385,4262264.246301935|2@[444874.360035869,4262219.5004118,444898.799734222,4262324.07001842]@@120981108035@" +
        "444898.799734222~444897.484191025~444881.571001148~444874.360035869~444875.350022455~444898.799734222=~~~~" +
        "4262323.14661558~4262219.5004118~4262229.2990487~4262229.27999384~4262324.07001842~4262323.14661558=";

    [Fact]
    public void Parses_a_parcel_reply_into_kaek_and_ring()
    {
        var shape = KtimanetReply.Parse(ParcelReply);

        Assert.NotNull(shape);
        Assert.Equal("120981108035", shape.Kaek);
        Assert.True(shape.IsLandParcel);
        var ring = Assert.Single(shape.Rings);
        Assert.Equal(6, ring.Count);
        Assert.Equal(new EgsaPoint(444881.571001148, 4262229.2990487), ring[2]);
    }

    [Fact]
    public void Nothing_at_the_point_parses_as_null()
    {
        Assert.Null(KtimanetReply.Parse("GETPSTKG|444900,4262420|"));
    }

    [Fact]
    public void Road_reply_with_several_rings_is_not_a_land_parcel()
    {
        var shape = KtimanetReply.Parse(
            "GETPSTKG|444900,4262212|21@[1,2,3,4]@@12098ΕΚ00029@1~2~2~1=5~6~6~5=~~~~1~1~2~1=5~5~6~5=");

        Assert.NotNull(shape);
        Assert.Equal("12098ΕΚ00029", shape.Kaek);
        Assert.False(shape.IsLandParcel);
        Assert.Equal(2, shape.Rings.Count);
        Assert.Equal(new EgsaPoint(6, 5), shape.Rings[1][1]);
    }

    [Fact]
    public void Mismatched_x_and_y_values_are_rejected_not_guessed()
    {
        Assert.Throws<FormatException>(() => KtimanetReply.Parse("GETPSTKG|1,2|2@[1,2,3,4]@@120981108035@1~2~3=~~~~1~2="));
    }

    [Fact]
    public void Area_matches_the_ktimatologio_print()
    {
        var shape = KtimanetReply.Parse(ParcelReply)!;

        Assert.Equal(2281.17, Math.Round(PolygonMath.AreaSqm(shape.Rings), 2));
    }

    [Fact]
    public void Egsa87_to_wgs84_matches_maptiler()
    {
        // api.maptiler.com/coordinates/transform/444898.799734222,4262323.14661558.json?s_srs=2100&t_srs=4326
        var p = Egsa87.ToWgs84(new EgsaPoint(444898.799734222, 4262323.14661558));

        Assert.Equal(23.369722857666037, p.Lon, 9);
        Assert.Equal(38.510176002298316, p.Lat, 9);
    }

    [Fact]
    public void Coverage_knows_points_inside_an_imported_parcel()
    {
        var shape = KtimanetReply.Parse(ParcelReply)!;
        var coverage = new Coverage();
        coverage.Add(shape.Rings.Select(r => (IReadOnlyList<LonLat>)r.Select(Egsa87.ToWgs84).ToList()).ToList());

        Assert.True(coverage.Contains(Egsa87.ToWgs84(new EgsaPoint(444885, 4262270))));
        Assert.False(coverage.Contains(Egsa87.ToWgs84(new EgsaPoint(444910, 4262260)))); // the neighbour to the east
    }

    [Fact]
    public void Hole_is_not_covered()
    {
        var outer = new List<LonLat> { new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0) };
        var hole = new List<LonLat> { new(4, 4), new(6, 4), new(6, 6), new(4, 6), new(4, 4) };

        Assert.False(PolygonMath.Contains(new[] { outer, hole }, new LonLat(5, 5)));
        Assert.True(PolygonMath.Contains(new[] { outer, hole }, new LonLat(2, 2)));
    }

    [Fact]
    public void Sweep_asks_the_coarse_grid_first_then_fills_in()
    {
        var grid = new SweepGrid(new MapExtent(0, 20, 20, 0), 5); // 4 x 4 points

        var points = grid.Points().ToList();

        Assert.Equal(16, grid.TotalPoints);
        Assert.Equal(16, points.Count);
        Assert.All(points.Take(4), p => Assert.Equal(1, p.Pass));
        Assert.Equal(new EgsaPoint(2.5, 17.5), points[0].Position); // top-left, half a step inside the view
    }

    [Fact]
    public void Sweep_skips_fine_points_surrounded_by_empty_coarse_points()
    {
        var grid = new SweepGrid(new MapExtent(0, 25, 25, 0), 5); // 5 x 5 points; coarse = cols/rows 0, 2, 4

        var asked = new List<GridPoint>();
        foreach (var p in grid.Points())
        {
            asked.Add(p);
            if (p.Pass == 1) grid.MarkEmpty(p); // the whole view is sea
        }

        // Coarse points are all asked; every fine point is surrounded by empty ones, so none is asked.
        Assert.Equal(9, asked.Count);
        Assert.All(asked, p => Assert.Equal(1, p.Pass));
    }

    [Fact]
    public void Sweep_still_asks_fine_points_next_to_land()
    {
        var grid = new SweepGrid(new MapExtent(0, 25, 25, 0), 5);

        var asked = new List<GridPoint>();
        foreach (var p in grid.Points())
        {
            asked.Add(p);
            if (p.Pass == 1 && !(p.Col == 4 && p.Row == 4)) grid.MarkEmpty(p); // only the bottom-right corner found land
        }

        var fine = asked.Where(p => p.Pass == 2).Select(p => (p.Col, p.Row)).ToHashSet();
        Assert.Equal(new HashSet<(int, int)> { (3, 3), (3, 4), (4, 3) }, fine);
    }
}
