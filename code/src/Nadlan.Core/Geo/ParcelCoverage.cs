using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;

namespace Nadlan.Core.Geo;

/// <summary>
/// How a zoomed-out map shows many Parcels: one united surface instead of thousands of polygons.
/// <see cref="CloseMetres"/> merges Parcels closer than twice this distance (roads, digitising slivers);
/// <see cref="SimplifyMetres"/> drops corners that would be sub-pixel at that zoom.
/// </summary>
public sealed record CoverageLevel(string Name, double CloseMetres, double SimplifyMetres)
{
    /// <summary>Region view (zoom below 12): blocks and villages as blobs.</summary>
    public static readonly CoverageLevel Overview = new("overview", 30, 20);

    /// <summary>Neighbourhood view (zoom 12 up to the detail zoom): blocks, with roads still visible.</summary>
    public static readonly CoverageLevel Mid = new("mid", 2, 2);

    public static readonly IReadOnlyList<CoverageLevel> All = new[] { Overview, Mid };
}

/// <summary>
/// Unites Parcel polygons into a surface (multipolygon): union, morphological closing (buffer out, then back in),
/// simplification. Works in local metres (equirectangular around the data's mean latitude), so distances mean the
/// same east-west and north-south; plenty accurate for display across Greece.
/// </summary>
public static class ParcelCoverageBuilder
{
    private static readonly GeometryFactory Factory = new();
    private static readonly BufferParameters CheapBuffer = new() { QuadrantSegments = 2, JoinStyle = NetTopologySuite.Operation.Buffer.JoinStyle.Mitre, MitreLimit = 2 };

    public static IReadOnlyList<GeoPolygon> Build(IReadOnlyList<GeoPolygon> parcels, CoverageLevel level)
    {
        if (parcels.Count == 0)
        {
            return Array.Empty<GeoPolygon>();
        }

        var lat0 = parcels.Average(p => p.Exterior[0].Lat) * Math.PI / 180;
        var kx = 111_320 * Math.Cos(lat0);
        const double ky = 110_540;

        var polygons = new List<Geometry>(parcels.Count);
        foreach (var parcel in parcels)
        {
            var polygon = ToPolygon(parcel, kx, ky);
            // A self-touching legacy polygon would break the union; buffer(0) is the standard repair.
            polygons.Add(polygon.IsValid ? polygon : polygon.Buffer(0));
        }

        Geometry surface = CascadedPolygonUnion.Union(polygons.Where(g => !g.IsEmpty).ToList()) ?? Factory.CreatePolygon();
        if (level.CloseMetres > 0)
        {
            surface = surface.Buffer(level.CloseMetres, CheapBuffer).Buffer(-level.CloseMetres, CheapBuffer);
        }

        if (level.SimplifyMetres > 0)
        {
            surface = TopologyPreservingSimplifier.Simplify(surface, level.SimplifyMetres);
        }

        // Specks smaller than a pixel at that zoom add vertices and nothing else.
        var minArea = Math.Max(1, level.SimplifyMetres * level.SimplifyMetres * 4);
        var result = new List<GeoPolygon>();
        for (var i = 0; i < surface.NumGeometries; i++)
        {
            if (surface.GetGeometryN(i) is Polygon p && p.Area >= minArea)
            {
                result.Add(FromPolygon(p, kx, ky, minArea));
            }
        }

        return result;
    }

    public static int VertexCount(IReadOnlyList<GeoPolygon> polygons) => polygons.Sum(p => p.Rings.Sum(r => r.Count));

    private static Polygon ToPolygon(GeoPolygon parcel, double kx, double ky)
    {
        LinearRing Ring(IReadOnlyList<GeoPoint> ring)
        {
            var coords = ring.Select(pt => new Coordinate(pt.Lon * kx, pt.Lat * ky)).ToList();
            if (!coords[0].Equals2D(coords[^1])) coords.Add(coords[0].Copy());
            return Factory.CreateLinearRing(coords.ToArray());
        }

        return Factory.CreatePolygon(Ring(parcel.Rings[0]), parcel.Rings.Skip(1).Select(Ring).ToArray());
    }

    private static GeoPolygon FromPolygon(Polygon polygon, double kx, double ky, double minHoleArea)
    {
        IReadOnlyList<GeoPoint> Ring(LineString ring) => ring.Coordinates.Select(c => new GeoPoint(c.X / kx, c.Y / ky)).ToList();

        var rings = new List<IReadOnlyList<GeoPoint>> { Ring(polygon.ExteriorRing) };
        rings.AddRange(polygon.InteriorRings.Where(h => Factory.CreatePolygon((LinearRing)h).Area >= minHoleArea).Select(Ring));
        return new GeoPolygon(rings);
    }
}
