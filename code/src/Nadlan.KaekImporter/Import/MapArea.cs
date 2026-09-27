using Nadlan.KaekImporter.Geometry;

namespace Nadlan.KaekImporter.Import;

/// <summary>The map view the user chose, in the Greek grid.</summary>
public sealed record MapExtent(double Left, double Right, double Top, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public EgsaPoint Centre => new((Left + Right) / 2, (Top + Bottom) / 2);
}

/// <summary>A point in an area, with its distance to the nearest parcel line.</summary>
public readonly record struct AreaPoint(EgsaPoint Position, double ClearanceMetres);

/// <summary>
/// One enclosed area between the yellow parcel lines: normally one parcel, road or the sea.
/// <see cref="Centre"/> is its point farthest from any line; <see cref="Samples"/> cover it every few metres.
/// </summary>
public sealed record MapArea(int SizePixels, AreaPoint Centre, IReadOnlyList<AreaPoint> Samples);

public sealed record MapAreas(MapExtent Extent, double MetresPerPixel, IReadOnlyList<MapArea> Areas);
