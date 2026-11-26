using NetTopologySuite.Geometries;

namespace Nadlan.Core.Geo;

/// <summary>
/// How different two Parcel polygons are, in square metres: the area of their symmetric difference (what one has and the
/// other hasn't) in local metres (equirectangular around the first polygon's first corner - Parcels are metres across, so
/// the error is far below a square metre). Used to tell a re-scraped Cadastre polygon from ours (migration 034).
/// </summary>
public static class GeoDifference
{
    private static readonly GeometryFactory Factory = new();

    /// <summary>The polygon's area in m² (exterior minus holes), in the same local metres.</summary>
    public static double AreaSqm(GeoPolygon polygon)
    {
        var (lon0, lat0, kx, ky) = Frame(polygon);
        return Valid(ToPolygon(polygon, lon0, lat0, kx, ky)).Area;
    }

    /// <summary>Area of (a ∪ b) − (a ∩ b), in m².</summary>
    public static double SymmetricDifferenceSqm(GeoPolygon a, GeoPolygon b)
    {
        var (lon0, lat0, kx, ky) = Frame(a);
        var pa = Valid(ToPolygon(a, lon0, lat0, kx, ky));
        var pb = Valid(ToPolygon(b, lon0, lat0, kx, ky));
        return pa.SymmetricDifference(pb).Area;
    }

    /// <summary>"The same polygon" for a Cadastre check: the difference is under max(1 m², 0.5 % of our polygon's area) (brief §3.2, default).</summary>
    public static double SameThresholdSqm(double currentAreaSqm) => Math.Max(1.0, 0.005 * currentAreaSqm);

    private static (double Lon0, double Lat0, double Kx, double Ky) Frame(GeoPolygon p)
    {
        var o = p.Exterior[0];
        return (o.Lon, o.Lat, 111_320 * Math.Cos(o.Lat * Math.PI / 180), 110_540);
    }

    private static Polygon ToPolygon(GeoPolygon p, double lon0, double lat0, double kx, double ky)
    {
        LinearRing Ring(IReadOnlyList<GeoPoint> ring) =>
            Factory.CreateLinearRing(ring.Select(pt => new Coordinate((pt.Lon - lon0) * kx, (pt.Lat - lat0) * ky)).ToArray());
        return Factory.CreatePolygon(Ring(p.Rings[0]), p.Rings.Skip(1).Select(Ring).ToArray());
    }

    // A self-touching polygon (legacy data) breaks the overlay; buffer(0) is the standard repair.
    private static Geometry Valid(Polygon p) => p.IsValid ? p : p.Buffer(0);
}
