namespace Nadlan.Core.Geo;

/// <summary>
/// Where Phase 1 Parcels can be (Greece only). Catches swapped lat/lon and corrupt coordinates early;
/// replace with per-country bounds when a second country arrives.
/// </summary>
public static class ServiceArea
{
    private const double MinLon = 19, MaxLon = 30, MinLat = 34, MaxLat = 42;

    public static bool Contains(GeoPoint p) => p.Lon is >= MinLon and <= MaxLon && p.Lat is >= MinLat and <= MaxLat;
}
