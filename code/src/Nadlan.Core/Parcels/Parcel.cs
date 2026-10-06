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

    /// <summary>
    /// Whether an Asset the CALLER may see stands on it (only filled by <see cref="IParcelStore.QueryAsync"/>): a
    /// competing Asset the user can't see must not show, not even as a colour (Scenario 17).
    /// </summary>
    public bool HasAssets { get; init; }

    /// <summary>How it is identified - its colour on the map (<see cref="ParcelKinds"/>).</summary>
    public string Kind => ParcelKinds.Of(RegistryIdIsProvisional, OT);
}

/// <summary>
/// How a Parcel is identified, which is its colour on the map and a map filter: a real KAEK; a provisional (TMP-) KAEK
/// whose OT (building block, with or without the plot number) is known; or a provisional one with no OT at all.
/// </summary>
public static class ParcelKinds
{
    public const string Kaek = "kaek";
    public const string Ot = "ot";
    public const string NoId = "noid";

    public static readonly IReadOnlyList<string> All = new[] { Kaek, Ot, NoId };

    public static string Of(bool provisional, string? ot) => !provisional ? Kaek : string.IsNullOrWhiteSpace(ot) ? NoId : Ot;
}
