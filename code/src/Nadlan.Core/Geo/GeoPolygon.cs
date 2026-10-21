using System.Globalization;
using System.Text;

namespace Nadlan.Core.Geo;

/// <summary>A WGS84 (EPSG:4326) coordinate. Longitude first, matching KML/GeoJSON order.</summary>
public readonly record struct GeoPoint(double Lon, double Lat);

/// <summary>
/// A WGS84 polygon. Rings[0] is the exterior ring; any further rings are holes.
/// Every ring is closed (first point == last point).
/// </summary>
public sealed class GeoPolygon
{
    public IReadOnlyList<IReadOnlyList<GeoPoint>> Rings { get; }

    public GeoPolygon(IReadOnlyList<IReadOnlyList<GeoPoint>> rings)
    {
        if (rings.Count == 0)
        {
            throw new ArgumentException("Polygon needs at least one ring.", nameof(rings));
        }

        foreach (var ring in rings)
        {
            if (ring.Count < 4)
            {
                throw new ArgumentException("A closed ring needs at least 4 points (3 distinct + closing point).", nameof(rings));
            }

            if (ring[0] != ring[^1])
            {
                throw new ArgumentException("Ring is not closed.", nameof(rings));
            }
        }

        Rings = rings;
    }

    public IReadOnlyList<GeoPoint> Exterior => Rings[0];

    /// <summary>
    /// Is the point inside (in the exterior ring and in no hole)? Plane math on lon/lat, ray casting - the same rule as
    /// the map's my-location.js containsPoint, so the server and the page agree (Parcels are metres across: no curvature).
    /// </summary>
    public bool Contains(GeoPoint p)
    {
        if (!InRing(Rings[0], p))
        {
            return false;
        }

        for (var h = 1; h < Rings.Count; h++)
        {
            if (InRing(Rings[h], p))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InRing(IReadOnlyList<GeoPoint> ring, GeoPoint p)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Lat > p.Lat) != (b.Lat > p.Lat) && p.Lon < (b.Lon - a.Lon) * (p.Lat - a.Lat) / (b.Lat - a.Lat) + a.Lon)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Bounding box: (west, south, east, north).</summary>
    public (double West, double South, double East, double North) Bounds()
    {
        double w = double.MaxValue, s = double.MaxValue, e = double.MinValue, n = double.MinValue;
        foreach (var p in Rings[0])
        {
            w = Math.Min(w, p.Lon);
            e = Math.Max(e, p.Lon);
            s = Math.Min(s, p.Lat);
            n = Math.Max(n, p.Lat);
        }

        return (w, s, e, n);
    }

    /// <summary>WKT in lon-lat order. Callers storing to MySQL must pass axis-order=long-lat.</summary>
    public string ToWkt()
    {
        var sb = new StringBuilder("POLYGON(");
        for (var r = 0; r < Rings.Count; r++)
        {
            if (r > 0)
            {
                sb.Append(',');
            }

            sb.Append('(');
            var ring = Rings[r];
            for (var i = 0; i < ring.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(ring[i].Lon.ToString("R", CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append(ring[i].Lat.ToString("R", CultureInfo.InvariantCulture));
            }

            sb.Append(')');
        }

        return sb.Append(')').ToString();
    }

    /// <summary>Parses POLYGON WKT written in lon-lat order.</summary>
    public static GeoPolygon FromWkt(string wkt)
    {
        var text = wkt.Trim();
        const string prefix = "POLYGON";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Only POLYGON WKT is supported.");
        }

        var body = text[prefix.Length..].Trim();
        if (body.Length < 4 || body[0] != '(' || body[^1] != ')')
        {
            throw new FormatException("Malformed POLYGON WKT.");
        }

        body = body[1..^1];
        var rings = new List<IReadOnlyList<GeoPoint>>();
        foreach (var ringText in body.Split(')', StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = ringText.Trim().TrimStart(',').Trim().TrimStart('(');
            if (cleaned.Length == 0)
            {
                continue;
            }

            var points = new List<GeoPoint>();
            foreach (var pair in cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = pair.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (xy.Length < 2)
                {
                    throw new FormatException($"Malformed WKT point '{pair}'.");
                }

                points.Add(new GeoPoint(
                    double.Parse(xy[0], CultureInfo.InvariantCulture),
                    double.Parse(xy[1], CultureInfo.InvariantCulture)));
            }

            rings.Add(points);
        }

        return new GeoPolygon(rings);
    }
}
