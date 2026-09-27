using Nadlan.KaekImporter.Geometry;

namespace Nadlan.KaekImporter.Import;

/// <summary>
/// Decides where to click next inside one area, so each parcel costs one request:
/// - first the area's centre, unless it is already inside a known parcel;
/// - then, only if a real part of the area is still not covered (a gap in the lines joined two parcels),
///   the uncovered sample farthest from the lines.
/// Points close to a line are ignored (the drawn line and the polygon edge differ by a pixel or two), and so is
/// anything within <see cref="ExclusionMetres"/> of a click that did not explain itself (nothing there, or a shape
/// that does not contain the click).
/// </summary>
public static class AreaProbePlanner
{
    public const double MinClearanceMetres = 1.0;
    public const double ExclusionMetres = 10.0;
    public const int MaxProbesPerArea = 4;

    /// <summary>Where to click next in this area, or null when it is done.</summary>
    public static EgsaPoint? NextProbe(MapArea area, Func<EgsaPoint, bool> isCovered, IReadOnlyCollection<EgsaPoint> excluded)
    {
        bool Open(EgsaPoint p) => !isCovered(p) && !excluded.Any(e => Distance(e, p) < ExclusionMetres);

        if (Open(area.Centre.Position))
        {
            return area.Centre.Position;
        }

        var clear = area.Samples.Where(s => s.ClearanceMetres >= MinClearanceMetres).ToList();
        var open = clear.Where(s => Open(s.Position)).ToList();
        if (open.Count < Math.Max(3, clear.Count / 10))
        {
            return null; // the rest is slivers along the lines
        }

        return open.MaxBy(s => s.ClearanceMetres).Position;
    }

    private static double Distance(EgsaPoint a, EgsaPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
