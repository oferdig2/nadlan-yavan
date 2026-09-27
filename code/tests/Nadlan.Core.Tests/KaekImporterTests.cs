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
    public void Wgs84_back_to_egsa87_round_trips_within_millimetres()
    {
        var original = new EgsaPoint(444898.799734222, 4262323.14661558);

        var back = Egsa87.FromWgs84(Egsa87.ToWgs84(original));

        Assert.InRange(Math.Abs(original.X - back.X), 0, 0.005); // series formulas: millimetre-level, far below a pixel
        Assert.InRange(Math.Abs(original.Y - back.Y), 0, 0.005);
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

    // A 20 x 20 m area centred on (10, 10), sampled every 5 m; clearance = distance to the nearest edge.
    private static MapArea Square(double originX = 0) => new(400, new AreaPoint(new EgsaPoint(originX + 10, 10), 10),
        (from x in new[] { 2.5, 7.5, 12.5, 17.5 }
         from y in new[] { 2.5, 7.5, 12.5, 17.5 }
         select new AreaPoint(new EgsaPoint(originX + x, y), Math.Min(Math.Min(x, 20 - x), Math.Min(y, 20 - y)))).ToList());

    [Fact]
    public void Area_is_first_clicked_in_its_middle()
    {
        var next = AreaProbePlanner.NextProbe(Square(), _ => false, Array.Empty<EgsaPoint>());

        Assert.Equal(new EgsaPoint(10, 10), next);
    }

    [Fact]
    public void Area_inside_a_known_parcel_needs_no_click()
    {
        Assert.Null(AreaProbePlanner.NextProbe(Square(), _ => true, Array.Empty<EgsaPoint>()));
    }

    [Fact]
    public void Uncovered_half_of_an_area_gets_its_own_click()
    {
        // The parcel found covers only the left half: the lines had a gap and joined two parcels into one area.
        var next = AreaProbePlanner.NextProbe(Square(), p => p.X <= 10, Array.Empty<EgsaPoint>());

        Assert.NotNull(next);
        Assert.True(next.Value.X > 10);
    }

    [Fact]
    public void Slivers_along_the_lines_are_not_clicked()
    {
        // Only samples hugging the lines (clearance below 1 m) are uncovered: drawn line vs polygon edge, not a parcel.
        var area = new MapArea(400, new AreaPoint(new EgsaPoint(10, 10), 10), new[]
        {
            new AreaPoint(new EgsaPoint(10, 10), 10), new AreaPoint(new EgsaPoint(0.5, 10), 0.5),
            new AreaPoint(new EgsaPoint(19.5, 10), 0.5), new AreaPoint(new EgsaPoint(10, 0.5), 0.5),
        });

        Assert.Null(AreaProbePlanner.NextProbe(area, p => Math.Abs(p.X - 10) < 1 && Math.Abs(p.Y - 10) < 1, Array.Empty<EgsaPoint>()));
    }

    [Fact]
    public void Nothing_is_clicked_within_10_m_of_a_click_that_found_nothing()
    {
        var excluded = new[] { new EgsaPoint(10, 10) };

        var next = AreaProbePlanner.NextProbe(Square(), _ => false, excluded);

        Assert.NotNull(next); // the corners are 10.6 m away, so still open
        Assert.True(Math.Sqrt((next.Value.X - 10) * (next.Value.X - 10) + (next.Value.Y - 10) * (next.Value.Y - 10)) >= AreaProbePlanner.ExclusionMetres);
        // An area elsewhere is not affected.
        Assert.Equal(new EgsaPoint(110, 10), AreaProbePlanner.NextProbe(Square(100), _ => false, excluded));
    }
}
