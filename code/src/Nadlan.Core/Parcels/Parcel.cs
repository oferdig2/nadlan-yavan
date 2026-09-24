using Nadlan.Core.Geo;

namespace Nadlan.Core.Parcels;

/// <summary>
/// Cadastral / physical land. Holds no business data (owner, price, status, portfolio) — that belongs to Asset.
/// </summary>
public sealed record Parcel
{
    public long ParcelId { get; init; }
    public int CountryId { get; init; }

    /// <summary>In Greece this is the KAEK.</summary>
    public string? RegistryId { get; init; }

    /// <summary>
    /// True when RegistryId was invented by the system (see <see cref="ProvisionalRegistryId"/>) because the real
    /// KAEK is not yet known. The UI must always show this; entering the real KAEK clears it.
    /// </summary>
    public bool RegistryIdIsProvisional { get; init; }

    public int? GeographicAreaId { get; init; }
    public required GeoPolygon Geometry { get; init; }
    public decimal? OfficialAreaSqm { get; init; }
    public string? OT { get; init; }
    public string? OTExt { get; init; }
    public string? PlotNumber { get; init; }
    public string? PlotExt { get; init; }

    /// <summary>Approximate slope, in percent.</summary>
    public decimal? Inclination { get; init; }

    public decimal? BuildFactor { get; init; }
    public string? Notes { get; init; }
    public long? CreatedByUserId { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
}
