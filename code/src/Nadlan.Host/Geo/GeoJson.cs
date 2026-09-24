using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Geo;

/// <summary>GeoJSON in/out for the browser. Coordinates are [lon, lat] (GeoJSON order).</summary>
internal static class GeoJson
{
    public static object Polygon(GeoPolygon polygon) => new
    {
        type = "Polygon",
        coordinates = polygon.Rings.Select(ring => ring.Select(p => new[] { p.Lon, p.Lat })),
    };

    /// <summary>Accepts GeoJSON Polygon coordinates; closes an open ring. Throws a validation error on bad shape.</summary>
    public static GeoPolygon ParsePolygon(double[][][]? coordinates)
    {
        if (coordinates is null || coordinates.Length == 0)
        {
            throw new DomainValidationException("GEOMETRY_REQUIRED", "Draw or paste the Parcel polygon.");
        }

        var rings = new List<IReadOnlyList<GeoPoint>>();
        foreach (var ring in coordinates)
        {
            if (ring is null)
            {
                throw new DomainValidationException("GEOMETRY_INVALID", "Polygon ring is empty.");
            }

            var points = new List<GeoPoint>();
            foreach (var pair in ring)
            {
                if (pair is null || pair.Length < 2)
                {
                    throw new DomainValidationException("GEOMETRY_INVALID", "Coordinates must be [longitude, latitude] pairs.");
                }

                var point = new GeoPoint(pair[0], pair[1]);
                if (!ServiceArea.Contains(point))
                {
                    throw new DomainValidationException("GEOMETRY_OUTSIDE_AREA",
                        $"Point {pair[0]}, {pair[1]} is outside Greece. Coordinates must be longitude, latitude (KML/GeoJSON order).");
                }

                points.Add(point);
            }

            if (points.Count > 0 && points[0] != points[^1])
            {
                points.Add(points[0]);
            }

            if (points.Count < 4)
            {
                throw new DomainValidationException("GEOMETRY_INVALID", "A polygon needs at least 3 corners.");
            }

            rings.Add(points);
        }

        return new GeoPolygon(rings);
    }

    /// <summary>Map area from query string: all four edges or none.</summary>
    public static GeoBounds? Bounds(double? west, double? south, double? east, double? north)
    {
        if (west is null && south is null && east is null && north is null)
        {
            return null;
        }

        if (west is not double w || south is not double s || east is not double e || north is not double n || w >= e || s >= n)
        {
            throw new DomainValidationException("INVALID_BOUNDS", "Map bounds need west < east and south < north.");
        }

        return new GeoBounds(w, s, e, n);
    }
}
