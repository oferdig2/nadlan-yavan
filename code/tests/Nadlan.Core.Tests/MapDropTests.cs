using Nadlan.Core.Files;
using Nadlan.Core.Geo;

namespace Nadlan.Core.Tests;

public class MapDropTests
{
    // Unit squares side by side along longitude: A = [0,1], B = [1,2], C = [2,3] (latitude 0..1).
    private static GeoPolygon Square(double west, double south, double size = 1)
        => new(new[] { new[] { new GeoPoint(west, south), new GeoPoint(west + size, south), new GeoPoint(west + size, south + size), new GeoPoint(west, south + size), new GeoPoint(west, south) } });

    private static readonly IReadOnlyList<(long Id, string? RegistryId, GeoPolygon Geometry)> Row = new[]
    {
        (1L, (string?)"A", Square(0, 0)), (2L, (string?)"B", Square(1, 0)), (3L, (string?)"C", Square(2, 0)),
    };

    // A flight east along latitude 0.5, one point every 500 ms (2 a second), from x0 to x1 over the given seconds.
    private static List<TrackPoint> Flight(double x0, double x1, int seconds)
    {
        var n = seconds * 2;
        return Enumerable.Range(0, n + 1).Select(i => new TrackPoint(i * 500, 0.5, x0 + (x1 - x0) * i / n, 30)).ToList();
    }

    [Fact]
    public void Polygon_contains_inside_points_and_not_holes()
    {
        var withHole = new GeoPolygon(new[]
        {
            Square(0, 0, 4).Exterior,
            Square(1, 1, 2).Exterior,
        });

        Assert.True(withHole.Contains(new GeoPoint(0.5, 0.5)));
        Assert.False(withHole.Contains(new GeoPoint(2, 2)));      // in the hole
        Assert.False(withHole.Contains(new GeoPoint(5, 5)));      // outside
        Assert.Equal((0, 0, 4, 4), withHole.Bounds());
    }

    [Fact]
    public void A_flight_over_three_parcels_gives_three_time_frames_in_order()
    {
        // 0.5 -> 2.5 in 20 s: A from 0 s to 5 s, B 5-15 s, C 15-20 s.
        var s = TrackSegmenter.Segment(Flight(0.5, 2.5, 20), Row);

        Assert.Equal(new long[] { 1, 2, 3 }, s.Select(x => x.ParcelId));
        Assert.Equal(0, s[0].StartMs);
        Assert.InRange(s[1].StartMs!.Value, 4500, 5500);
        Assert.InRange(s[1].EndMs!.Value, 14500, 15500);
        Assert.Equal(20000, s[2].EndMs);
        Assert.True(s[1].LongLook);          // 10 s over B
        Assert.Equal("B", s[1].RegistryId);
    }

    [Fact]
    public void Passing_over_is_not_a_long_look()
    {
        // 6 s over A, then 2 s over B, then 6 s over C: B is "passed over".
        var points = Flight(0.5, 1.0, 6).Concat(Flight(1.0, 2.0, 2).Skip(1).Select(p => p with { Ms = p.Ms + 6000 }))
            .Concat(Flight(2.0, 2.5, 6).Skip(1).Select(p => p with { Ms = p.Ms + 8000 })).ToList();

        var s = TrackSegmenter.Segment(points, Row);

        var b = Assert.Single(s, x => x.ParcelId == 2);
        Assert.False(b.LongLook);
        Assert.All(s.Where(x => x.ParcelId != 2), x => Assert.True(x.LongLook));
    }

    [Fact]
    public void Gps_flicker_at_a_boundary_is_folded_in()
    {
        // Hovering over A near its edge; one 500 ms reading lands in B.
        var points = Enumerable.Range(0, 20).Select(i => new TrackPoint(i * 500, 0.5, i == 10 ? 1.01 : 0.99, 30)).ToList();

        var s = TrackSegmenter.Segment(points, Row);

        var a = Assert.Single(s);
        Assert.Equal(1, a.ParcelId);
        Assert.Equal(0, a.StartMs);
        Assert.Equal(9500, a.EndMs);
    }

    [Fact]
    public void A_parcel_flown_over_twice_gets_two_frames()
    {
        // A (5 s) -> B (5 s) -> back to A (5 s).
        var points = Flight(0.5, 1.5, 10).Concat(Flight(1.5, 0.5, 10).Skip(1).Select(p => p with { Ms = p.Ms + 10000 })).ToList();

        var s = TrackSegmenter.Segment(points, Row);

        Assert.Equal(2, s.Count(x => x.ParcelId == 1));
        Assert.Single(s, x => x.ParcelId == 2);
    }

    [Fact]
    public void Overlapping_parcels_both_get_the_frame_and_gaps_get_nothing()
    {
        var overlapping = Row.Append((4L, (string?)"A2", Square(0, 0))).ToList();
        var points = Flight(0.5, 5.5, 10); // A/A2 at first, then B, C, then outside every parcel

        var s = TrackSegmenter.Segment(points, overlapping);

        Assert.Contains(s, x => x.ParcelId == 1);
        Assert.Contains(s, x => x.ParcelId == 4);
        Assert.Equal(s.Single(x => x.ParcelId == 1).EndMs, s.Single(x => x.ParcelId == 4).EndMs);
        Assert.DoesNotContain(s, x => x.ParcelId is not (1 or 2 or 3 or 4));
        Assert.True(s.Max(x => x.EndMs) < 10000); // the stretch over no parcel isn't a frame
    }

    [Fact]
    public void No_points_or_no_parcels_give_no_frames()
    {
        Assert.Empty(TrackSegmenter.Segment(Array.Empty<TrackPoint>(), Row));
        Assert.Empty(TrackSegmenter.Segment(Flight(5, 6, 4), Row));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65_400, "1:05")]
    [InlineData(3_725_000, "1:02:05")]
    public void Times_are_shown_as_minutes_and_seconds(int ms, string expected)
        => Assert.Equal(expected, MapDropService.FormatTime(ms));
}
