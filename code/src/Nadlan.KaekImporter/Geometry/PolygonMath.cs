namespace Nadlan.KaekImporter.Geometry;

public static class PolygonMath
{
    /// <summary>
    /// Even-odd rule over all rings, so a hole is "outside" and a multi-part shape counts each part.
    /// Rings are closed (first point repeated last).
    /// </summary>
    public static bool Contains(IReadOnlyList<IReadOnlyList<LonLat>> rings, LonLat p)
    {
        var inside = false;
        foreach (var ring in rings)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.Lat > p.Lat) != (b.Lat > p.Lat)
                    && p.Lon < (b.Lon - a.Lon) * (p.Lat - a.Lat) / (b.Lat - a.Lat) + a.Lon)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    /// <summary>
    /// Planar area in the Greek grid: first ring minus the others (holes). This is what the Ktimatologio print
    /// shows as "Εμβαδόν" (e.g. 2281.17 m² for 120981108035).
    /// </summary>
    public static double AreaSqm(IReadOnlyList<IReadOnlyList<EgsaPoint>> rings)
    {
        var area = 0.0;
        for (var r = 0; r < rings.Count; r++)
        {
            var ringArea = Math.Abs(Shoelace(rings[r]));
            area += r == 0 ? ringArea : -ringArea;
        }

        return Math.Max(area, 0);
    }

    private static double Shoelace(IReadOnlyList<EgsaPoint> ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            sum += ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
        }

        return sum / 2;
    }
}
