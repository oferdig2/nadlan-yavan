using System.Globalization;

namespace Nadlan.Core.Geo;

public sealed record KmlParseResult(GeoPolygon? Polygon, IReadOnlyList<string> Warnings, string? Error)
{
    public bool Success => Polygon is not null;
}

/// <summary>
/// Parses a KML &lt;coordinates&gt; body: whitespace-separated "lon,lat[,alt]" tuples forming one ring.
/// </summary>
public static class KmlCoordinates
{
    public static KmlParseResult ParseRing(string? text)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new KmlParseResult(null, warnings, "No coordinates.");
        }

        var points = new List<GeoPoint>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(',');
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            {
                return new KmlParseResult(null, warnings, $"Unreadable coordinate '{token}'.");
            }

            var point = new GeoPoint(lon, lat);
            if (!ServiceArea.Contains(point))
            {
                return new KmlParseResult(null, warnings, $"Coordinate {lon},{lat} is outside Greece.");
            }

            if (points.Count > 0 && points[^1] == point)
            {
                continue; // drop consecutive duplicates; they add nothing and can upset validity checks
            }

            points.Add(point);
        }

        if (points.Count > 1 && points[0] != points[^1])
        {
            points.Add(points[0]);
            warnings.Add("Ring was not closed; closing point added.");
        }

        if (points.Count < 4)
        {
            return new KmlParseResult(null, warnings, "Fewer than 3 distinct points.");
        }

        return new KmlParseResult(new GeoPolygon(new[] { points }), warnings, null);
    }
}
