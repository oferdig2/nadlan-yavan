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

    /// <summary>Whether its OT / plot number is entered - its colour on the map (<see cref="ParcelKinds"/>).</summary>
    public string Kind => ParcelKinds.Of(OT, PlotNumber);
}

/// <summary>
/// How far a Parcel's OT / plot data entry is, which is its colour on the map and a map filter (customer change
/// request #1: green = entered, blue = still to do): both OT and plot number; only one of them; neither.
/// Real vs provisional KAEK is a separate filter (<see cref="ParcelQuery.Provisional"/>), not a colour.
/// </summary>
public static class ParcelKinds
{
    public const string Done = "done";
    public const string Partial = "partial";
    public const string Todo = "todo";

    public static readonly IReadOnlyList<string> All = new[] { Done, Partial, Todo };

    public static string Of(string? ot, string? plot) => (string.IsNullOrWhiteSpace(ot), string.IsNullOrWhiteSpace(plot)) switch
    {
        (false, false) => Done,
        (true, true) => Todo,
        _ => Partial,
    };
}
