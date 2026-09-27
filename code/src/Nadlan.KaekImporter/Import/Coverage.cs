using Nadlan.KaekImporter.Geometry;

namespace Nadlan.KaekImporter.Import;

/// <summary>Shapes we already know about; a sweep point inside one needs no request to the site.</summary>
public sealed class Coverage
{
    private readonly List<(double MinLon, double MinLat, double MaxLon, double MaxLat, IReadOnlyList<IReadOnlyList<LonLat>> Rings)> _shapes = new();

    public void Add(IReadOnlyList<IReadOnlyList<LonLat>> rings)
    {
        var all = rings.SelectMany(r => r).ToList();
        if (all.Count == 0) return;
        _shapes.Add((all.Min(p => p.Lon), all.Min(p => p.Lat), all.Max(p => p.Lon), all.Max(p => p.Lat), rings));
    }

    public bool Contains(LonLat p)
    {
        foreach (var s in _shapes)
        {
            if (p.Lon >= s.MinLon && p.Lon <= s.MaxLon && p.Lat >= s.MinLat && p.Lat <= s.MaxLat && PolygonMath.Contains(s.Rings, p))
            {
                return true;
            }
        }

        return false;
    }
}
