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

    /// <summary>
    /// The point inside the polygon farthest from its edges (the pole of inaccessibility, as Mapbox "polylabel"), found to
    /// within <paramref name="precisionMetres"/>: where a check of an existing Parcel clicks, well inside it even when the
    /// Parcel is L- or U-shaped (its centroid may lie outside). Null when the polygon has no inside (degenerate).
    /// Rings in the Greek grid, first ring the outline, the others holes.
    /// </summary>
    public static (EgsaPoint Point, double ClearanceMetres)? InsidePoint(IReadOnlyList<IReadOnlyList<EgsaPoint>> rings, double precisionMetres = 0.25)
    {
        if (rings.Count == 0 || rings[0].Count < 4) return null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in rings[0])
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }

        var size = Math.Min(maxX - minX, maxY - minY);
        if (!(size > 0)) return null;

        var queue = new PriorityQueue<(double X, double Y, double Half, double D), double>();
        void Push(double x, double y, double half)
        {
            var d = SignedDistance(rings, x, y);
            queue.Enqueue((x, y, half, d), -(d + half * Math.Sqrt(2))); // the most promising cell first
        }

        var cell = size / 2;
        for (var x = minX; x < maxX; x += size)
        {
            for (var y = minY; y < maxY; y += size)
            {
                Push(x + cell, y + cell, cell);
            }
        }

        var (cx, cy) = Centroid(rings[0]);
        var best = (X: cx, Y: cy, D: SignedDistance(rings, cx, cy));
        var mid = (X: minX + (maxX - minX) / 2, Y: minY + (maxY - minY) / 2);
        var midD = SignedDistance(rings, mid.X, mid.Y);
        if (midD > best.D) best = (mid.X, mid.Y, midD);

        for (var steps = 0; queue.Count > 0 && steps < 100_000; steps++)
        {
            var c = queue.Dequeue();
            if (c.D > best.D) best = (c.X, c.Y, c.D);
            if (c.D + c.Half * Math.Sqrt(2) - best.D <= precisionMetres) continue; // can't do better here
            var h = c.Half / 2;
            Push(c.X - h, c.Y - h, h);
            Push(c.X + h, c.Y - h, h);
            Push(c.X - h, c.Y + h, h);
            Push(c.X + h, c.Y + h, h);
        }

        return best.D > 0 ? (new EgsaPoint(best.X, best.Y), best.D) : null;
    }

    /// <summary>Distance to the nearest edge: positive inside (even-odd over all rings, so holes are outside), negative outside.</summary>
    private static double SignedDistance(IReadOnlyList<IReadOnlyList<EgsaPoint>> rings, double x, double y)
    {
        var inside = false;
        var min = double.MaxValue;
        foreach (var ring in rings)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                min = Math.Min(min, SegmentDistance(x, y, a, b));
            }
        }

        return inside ? min : -min;
    }

    private static double SegmentDistance(double x, double y, EgsaPoint a, EgsaPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len, 0, 1);
        double px = a.X + t * dx - x, py = a.Y + t * dy - y;
        return Math.Sqrt(px * px + py * py);
    }

    private static (double X, double Y) Centroid(IReadOnlyList<EgsaPoint> ring)
    {
        double area = 0, x = 0, y = 0;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            var f = ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
            x += (ring[i].X + ring[i + 1].X) * f;
            y += (ring[i].Y + ring[i + 1].Y) * f;
            area += f * 3;
        }

        return area == 0 ? (ring[0].X, ring[0].Y) : (x / area, y / area);
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
