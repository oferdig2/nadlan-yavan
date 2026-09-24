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
