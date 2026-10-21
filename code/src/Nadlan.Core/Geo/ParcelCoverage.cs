using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;

namespace Nadlan.Core.Geo;

/// <summary>
/// How a zoomed-out map shows many Parcels: one united surface instead of thousands of polygons.
/// <see cref="CloseMetres"/> merges Parcels closer than twice this distance (roads, digitising slivers);
/// <see cref="SimplifyMetres"/> drops corners that would be sub-pixel at that zoom. If the result has more corners than
/// <see cref="VertexBudget"/>, both are doubled until it fits (more Parcels = coarser surface, never a slow map).
/// </summary>
public sealed record CoverageLevel(string Name, double CloseMetres, double SimplifyMetres, int VertexBudget)
{
    /// <summary>Region view (zoom below 12): blocks and villages as blobs.</summary>
    public static readonly CoverageLevel Overview = new("overview", 30, 20, 20_000);

    /// <summary>Neighbourhood view (zoom 12 up to the detail zoom): blocks, with roads still visible.</summary>
    public static readonly CoverageLevel Mid = new("mid", 2, 2, 60_000);

    /// <summary>
    /// Detail zoom, but more Parcels in view than the browser draws one by one: close to the real outlines. No closing
    /// (buffer out and back in): at this zoom a 0.5 m sliver is invisible, and closing was the step that ran the server
    /// out of memory (2026-10-21, 13,952 Parcels under the 200 MB heap limit).
    /// </summary>
    public static readonly CoverageLevel Fine = new("fine", 0, 0.5, 150_000);

    public static readonly IReadOnlyList<CoverageLevel> All = new[] { Overview, Mid, Fine };
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

        Geometry union = CascadedPolygonUnion.Union(polygons.Where(g => !g.IsEmpty).ToList()) ?? Factory.CreatePolygon();

        // Too heavy for the browser? Merge and simplify harder (x2 each time) until it fits the level's budget.
        for (var attempt = 0; ; attempt++)
        {
            var result = Finish(union, level, kx, ky);
            if (VertexCount(result) <= level.VertexBudget || attempt == MaxEscalations)
            {
                return result;
            }

            // A level without closing (Fine) stays without: only simplify harder.
            level = level with { CloseMetres = level.CloseMetres > 0 ? Math.Max(1, level.CloseMetres * 2) : 0, SimplifyMetres = Math.Max(1, level.SimplifyMetres * 2) };
        }
    }

    private const int MaxEscalations = 6;

    private static List<GeoPolygon> Finish(Geometry union, CoverageLevel level, double kx, double ky)
    {
        var surface = union;
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

/// <summary>
/// The zoomed-out surfaces built one geographic area at a time (owner, 2026-10-21): Paros, Anti Paros, Skroponeria,
/// Karystos ... are kilometres apart, so uniting them together gains nothing, while uniting all of Greece at once needed
/// more memory than the server has. Only one area's Parcels are in memory at a time; the finished surfaces are small.
/// Each area gets its share of a level's vertex budget (by Parcel count), so the browser gets about as much as before.
/// </summary>
public static class ParcelCoverageByArea
{
    /// <summary>The smallest budget an area gets: a small area's surface still keeps its shape.</summary>
    public const int MinAreaVertexBudget = 2_000;

    /// <summary>level name -> kind -> surface polygons (all areas together).</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, List<GeoPolygon>>>> BuildAsync(
        IReadOnlyList<(int? AreaId, long Count)> areas, IReadOnlyList<string> kinds, IReadOnlyList<CoverageLevel> levels,
        Func<int?, CancellationToken, Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>>> loadArea, CancellationToken ct = default)
    {
        var result = levels.ToDictionary(l => l.Name, _ => kinds.ToDictionary(k => k, _ => new List<GeoPolygon>()));
        var total = Math.Max(1, areas.Sum(a => a.Count));
        foreach (var area in areas.Where(a => a.Count > 0))
        {
            ct.ThrowIfCancellationRequested();
            var parcels = await loadArea(area.AreaId, ct);
            var share = (double)area.Count / total;
            foreach (var level in levels)
            {
                var budget = Math.Max(MinAreaVertexBudget, (int)(level.VertexBudget * share));
                foreach (var kind in kinds)
                {
                    var ofKind = parcels.Where(p => p.Kind == kind).Select(p => p.Geometry).ToList();
                    if (ofKind.Count > 0)
                    {
                        result[level.Name][kind].AddRange(ParcelCoverageBuilder.Build(ofKind, level with { VertexBudget = budget }));
                    }
                }
            }
        }

        return result.ToDictionary(l => l.Key, l => (IReadOnlyDictionary<string, List<GeoPolygon>>)l.Value);
    }
}
