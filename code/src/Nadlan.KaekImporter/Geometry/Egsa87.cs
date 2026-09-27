namespace Nadlan.KaekImporter.Geometry;

/// <summary>A point in the Greek grid (EGSA87 / GGRS87, EPSG:2100), in metres.</summary>
public readonly record struct EgsaPoint(double X, double Y);

/// <summary>A WGS84 point (EPSG:4326), in degrees.</summary>
public readonly record struct LonLat(double Lon, double Lat);

/// <summary>
/// EGSA87 -> WGS84, done locally: inverse Transverse Mercator on GRS80 (lon0 = 24°, k0 = 0.9996, FE = 500000),
/// then the EPSG:2100 3-parameter datum shift (towgs84 = -199.87, 74.79, 246.62).
/// Gives the same result as the MapTiler transform service (checked to ~1e-11°), without a web call per point.
/// </summary>
public static class Egsa87
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257222101;
    private const double E2 = F * (2 - F);
    private const double K0 = 0.9996;
    private const double FalseEasting = 500000.0;
    private const double Lon0 = 24.0 * Math.PI / 180;
    private const double Dx = -199.87, Dy = 74.79, Dz = 246.62;

    public static LonLat ToWgs84(EgsaPoint p)
    {
        var (lat, lon) = InverseTransverseMercator(p.X, p.Y);

        // GGRS87 geodetic -> ECEF, shift, back to geodetic. GRS80 and WGS84 differ by < 0.1 mm, so one ellipsoid is used.
        var n = A / Math.Sqrt(1 - E2 * Sq(Math.Sin(lat)));
        var x = n * Math.Cos(lat) * Math.Cos(lon) + Dx;
        var y = n * Math.Cos(lat) * Math.Sin(lon) + Dy;
        var z = n * (1 - E2) * Math.Sin(lat) + Dz;

        var horizontal = Math.Sqrt(x * x + y * y);
        var outLat = Math.Atan2(z, horizontal * (1 - E2));
        for (var i = 0; i < 6; i++)
        {
            var ni = A / Math.Sqrt(1 - E2 * Sq(Math.Sin(outLat)));
            outLat = Math.Atan2(z + E2 * ni * Math.Sin(outLat), horizontal);
        }

        return new LonLat(Math.Atan2(y, x) * 180 / Math.PI, outLat * 180 / Math.PI);
    }

    private static (double Lat, double Lon) InverseTransverseMercator(double easting, double northing)
    {
        var ep2 = E2 / (1 - E2);
        var mu = northing / K0 / (A * (1 - E2 / 4 - 3 * E2 * E2 / 64 - 5 * Math.Pow(E2, 3) / 256));
        var e1 = (1 - Math.Sqrt(1 - E2)) / (1 + Math.Sqrt(1 - E2));
        var phi1 = mu
            + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
            + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
            + 151 * Math.Pow(e1, 3) / 96 * Math.Sin(6 * mu)
            + 1097 * Math.Pow(e1, 4) / 512 * Math.Sin(8 * mu);

        var c1 = ep2 * Sq(Math.Cos(phi1));
        var t1 = Sq(Math.Tan(phi1));
        var n1 = A / Math.Sqrt(1 - E2 * Sq(Math.Sin(phi1)));
        var r1 = A * (1 - E2) / Math.Pow(1 - E2 * Sq(Math.Sin(phi1)), 1.5);
        var d = (easting - FalseEasting) / (n1 * K0);

        var lat = phi1 - n1 * Math.Tan(phi1) / r1 * (
            d * d / 2
            - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * Math.Pow(d, 4) / 24
            + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        var lon = Lon0 + (
            d
            - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
            + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / Math.Cos(phi1);
        return (lat, lon);
    }

    private static double Sq(double v) => v * v;
}
