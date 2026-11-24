using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
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
    /// <summary>
    /// Which rules made a surface. A saved surface (coverage.json on the server) made by other rules is ignored and rebuilt:
    /// bump this whenever the levels, the tiling or the steps below change.
    /// </summary>
    public const int Version = 2;

    private static readonly GeometryFactory Factory = new();
    private static readonly BufferParameters CheapBuffer = new() { QuadrantSegments = 2, JoinStyle = NetTopologySuite.Operation.Buffer.JoinStyle.Mitre, MitreLimit = 2 };

    public static IReadOnlyList<GeoPolygon> Build(IReadOnlyList<GeoPolygon> parcels, CoverageLevel level)
    {
        if (parcels.Count == 0)
        {
            return Array.Empty<GeoPolygon>();
        }

        var (kx, ky) = MetresPerDegree(parcels);
        return Surface(Union(parcels, kx, ky), level, kx, ky);
    }

    /// <summary>Local metres per degree (east, north) around the polygons' mean latitude.</summary>
    internal static (double Kx, double Ky) MetresPerDegree(IEnumerable<GeoPolygon> parcels)
    {
        var lat0 = parcels.Average(p => p.Exterior[0].Lat) * Math.PI / 180;
        return (111_320 * Math.Cos(lat0), 110_540);
    }

    /// <summary>
    /// The polygons united, in local metres. The converted input is only alive inside this method: the union is all that
    /// the levels need, so the caller unites once and finishes it for each level.
    /// </summary>
    internal static Geometry Union(IReadOnlyList<GeoPolygon> parcels, double kx, double ky)
    {
        var polygons = new List<Geometry>(parcels.Count);
        foreach (var parcel in parcels)
        {
            var polygon = ToPolygon(parcel, kx, ky);
            // A self-touching legacy polygon would break the union; buffer(0) is the standard repair.
            var valid = polygon.IsValid ? polygon : polygon.Buffer(0);
            if (!valid.IsEmpty)
            {
                polygons.Add(valid);
            }
        }

        return CascadedPolygonUnion.Union(polygons) ?? Factory.CreatePolygon();
    }

    /// <summary>The union closed and simplified for one level; harder while over the level's vertex budget.</summary>
    internal static List<GeoPolygon> Surface(Geometry union, CoverageLevel level, double kx, double ky)
    {
        // Too heavy for the browser? Merge and simplify harder (x2 each time) until it fits the level's budget - or until
        // harder no longer helps (Fine on Parcels that are simple squares already: six more rounds would only cost time).
        var previous = int.MaxValue;
        for (var attempt = 0; ; attempt++)
        {
            var result = Finish(union, level, kx, ky);
            var vertices = VertexCount(result);
            if (vertices <= level.VertexBudget || attempt == MaxEscalations || (level.CloseMetres == 0 && vertices >= previous))
            {
                return result;
            }

            previous = vertices;

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
/// A big area is built in square tiles (<see cref="TileParcels"/>): Athens alone (16,643 Parcels) ran the closing step
/// out of the 200 MB heap on 2026-11-24. Only one tile's geometries exist at a time; the tiles' finished surfaces are
/// then united and finished once more, so no seam shows where two tiles meet.
/// </summary>
public static class ParcelCoverageByArea
{
    /// <summary>The smallest budget an area gets: a small area's surface still keeps its shape.</summary>
    public const int MinAreaVertexBudget = 2_000;

    /// <summary>An area (one kind of it) with more Parcels than this is built in tiles of at most this many.</summary>
    public const int TileParcels = 2_000;

    /// <summary>First tile size; a tile that still holds more than <see cref="TileParcels"/> is split in four.</summary>
    public const double TileMetres = 2_000;

    private const double MinTileMetres = 100;
    private const int MinTileVertexBudget = 300;

    /// <summary>level name -> kind -> surface polygons (all areas together).</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, List<GeoPolygon>>>> BuildAsync(
        IReadOnlyList<(int? AreaId, long Count)> areas, IReadOnlyList<string> kinds, IReadOnlyList<CoverageLevel> levels,
        Func<int?, CancellationToken, Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>>> loadArea, CancellationToken ct = default,
        int tileParcels = TileParcels)
    {
        var result = levels.ToDictionary(l => l.Name, _ => kinds.ToDictionary(k => k, _ => new List<GeoPolygon>()));
        var total = Math.Max(1, areas.Sum(a => a.Count));
        foreach (var area in areas.Where(a => a.Count > 0))
        {
            ct.ThrowIfCancellationRequested();
            var parcels = await loadArea(area.AreaId, ct);
            if (parcels.Count == 0)
            {
                continue;
            }

            var share = (double)area.Count / total;
            var budgets = levels.ToDictionary(l => l.Name, l => Math.Max(MinAreaVertexBudget, (int)(l.VertexBudget * share)));
            var (kx, ky) = ParcelCoverageBuilder.MetresPerDegree(parcels.Select(p => p.Geometry));
            foreach (var kind in kinds)
            {
                var ofKind = parcels.Where(p => p.Kind == kind).Select(p => p.Geometry).ToList();
                if (ofKind.Count == 0)
                {
                    continue;
                }

                // One union per tile, finished for every level (the union is the slow part); a tile's budget is its share
                // of the area's by Parcel count.
                var tiles = Tiles(ofKind, kx, ky, tileParcels);
                var pieces = levels.ToDictionary(l => l.Name, _ => new List<(int Tile, GeoPolygon Piece)>());
                for (var t = 0; t < tiles.Count; t++)
                {
                    ct.ThrowIfCancellationRequested();
                    var tile = tiles[t];
                    var union = ParcelCoverageBuilder.Union(tile, kx, ky);
                    foreach (var level in levels)
                    {
                        var budget = tiles.Count == 1
                            ? budgets[level.Name]
                            : Math.Max(MinTileVertexBudget, (int)((long)budgets[level.Name] * tile.Count / ofKind.Count));
                        var tileIndex = t;
                        pieces[level.Name].AddRange(ParcelCoverageBuilder.Surface(union, level with { VertexBudget = budget }, kx, ky).Select(p => (tileIndex, p)));
                    }
                }

                foreach (var level in levels)
                {
                    result[level.Name][kind].AddRange(tiles.Count == 1
                        ? pieces[level.Name].Select(p => p.Piece)
                        : Stitch(pieces[level.Name], level with { VertexBudget = budgets[level.Name] }, kx, ky));
                }
            }
        }

        return result.ToDictionary(l => l.Key, l => (IReadOnlyDictionary<string, List<GeoPolygon>>)l.Value);
    }

    /// <summary>
    /// Joins the tiles where they meet: the pieces that come within closing reach of a piece of another tile are united and
    /// finished again; every other piece is final already. (Uniting all pieces again took 27 s for 13,300 separate Fine
    /// pieces, against 5 s for all the tiles.)
    /// </summary>
    internal static List<GeoPolygon> Stitch(List<(int Tile, GeoPolygon Piece)> pieces, CoverageLevel level, double kx, double ky)
    {
        var reach = 2 * level.CloseMetres + 0.01; // closing with radius r joins gaps narrower than 2r
        var envelopes = pieces.Select(p => EnvelopeOf(p.Piece, kx, ky, reach)).ToArray();
        var index = new STRtree<int>();
        for (var i = 0; i < pieces.Count; i++)
        {
            index.Insert(envelopes[i], i);
        }

        var atSeam = new bool[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
        {
            foreach (var j in index.Query(envelopes[i]))
            {
                if (pieces[j].Tile != pieces[i].Tile)
                {
                    atSeam[i] = atSeam[j] = true;
                }
            }
        }

        var result = pieces.Where((_, i) => !atSeam[i]).Select(p => p.Piece).ToList();
        var seam = pieces.Where((_, i) => atSeam[i]).Select(p => p.Piece).ToList();
        if (seam.Count > 0)
        {
            var budget = Math.Max(MinTileVertexBudget, level.VertexBudget - ParcelCoverageBuilder.VertexCount(result));
            result.AddRange(ParcelCoverageBuilder.Surface(ParcelCoverageBuilder.Union(seam, kx, ky), level with { VertexBudget = budget }, kx, ky));
        }

        return result;
    }

    private static Envelope EnvelopeOf(GeoPolygon polygon, double kx, double ky, double grow)
    {
        var envelope = new Envelope();
        foreach (var pt in polygon.Exterior)
        {
            envelope.ExpandToInclude(pt.Lon * kx, pt.Lat * ky);
        }

        envelope.ExpandBy(grow);
        return envelope;
    }

    /// <summary>
    /// Square tiles by each Parcel's first corner: 2 km, a tile with more than <paramref name="maxParcels"/> split in four
    /// (down to 100 m). Fewer Parcels than that: one tile, the whole list.
    /// </summary>
    internal static List<List<GeoPolygon>> Tiles(List<GeoPolygon> parcels, double kx, double ky, int maxParcels)
    {
        var tiles = new List<List<GeoPolygon>>();
        if (parcels.Count <= maxParcels)
        {
            tiles.Add(parcels);
            return tiles;
        }

        Split(parcels, TileMetres);
        return tiles;

        void Split(List<GeoPolygon> group, double size)
        {
            var cells = group.GroupBy(p => (Y: Math.Floor(p.Exterior[0].Lat * ky / size), X: Math.Floor(p.Exterior[0].Lon * kx / size)))
                             .OrderBy(c => c.Key.Y).ThenBy(c => c.Key.X);
            foreach (var cell in cells)
            {
                var list = cell.ToList();
                if (list.Count > maxParcels && size / 2 >= MinTileMetres)
                {
                    Split(list, size / 2);
                }
                else
                {
                    tiles.Add(list);
                }
            }
        }
    }
}
