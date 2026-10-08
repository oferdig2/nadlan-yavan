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

    /// <summary>Who last entered or changed OT / plot (customer change request #1: data entry is checked per person), and when.</summary>
    public long? OtPlotByUserId { get; init; }

    public DateTime? OtPlotUpdatedUtc { get; init; }

    /// <summary>That user's display name (read only, filled by the store; null if unknown or deleted).</summary>
    public string? OtPlotByName { get; init; }

    /// <summary>Regular, divided or united (<see cref="ParcelDivision"/>).</summary>
    public string DivisionStatus { get; init; } = ParcelDivision.Regular;

    /// <summary>Divided / united only: the other OT / plot numbers, free text as typed (V1: shown, not searched).</summary>
    public string? RelatedNumbers { get; init; }

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
    public string Kind => ParcelKinds.Of(OT, OTExt, PlotNumber, PlotExt);
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

    /// <summary>Entered = has a search key (<see cref="ParcelNumberKey"/>): blanks and lone separators such as "-" are not a number.</summary>
    public static string Of(string? ot, string? otExt, string? plot, string? plotExt) => (ParcelNumberKey.Key(ot, otExt) is null, ParcelNumberKey.Key(plot, plotExt) is null) switch
    {
        (false, false) => Done,
        (true, true) => Todo,
        _ => Partial,
    };
}

/// <summary>
/// Whether a Parcel was divided or united (owner, 2026-10-08). For divided / united ones the user notes the other OT / plot
/// numbers as free text (<see cref="Parcel.RelatedNumbers"/>); V1 doesn't search it - it may become structured later.
/// </summary>
public static class ParcelDivision
{
    public const string Regular = "regular";
    public const string Divided = "divided";
    public const string United = "united";

    public static readonly IReadOnlyList<string> All = new[] { Regular, Divided, United };

    public const int MaxRelatedNumbersLength = 500; // parcel.related_numbers VARCHAR(500)

    /// <summary>
    /// The status and text to store. A null status keeps the current one (API clients that don't know the field), and so
    /// does a null text then; a regular Parcel has no text.
    /// </summary>
    public static (string Status, string? RelatedNumbers) Resolve(string? status, string? relatedNumbers, string currentStatus, string? currentRelated)
    {
        var s = status is null ? currentStatus : status.Trim().ToLowerInvariant();
        if (!All.Contains(s))
        {
            throw new Validation.DomainValidationException("PARCEL_DIVISION_INVALID", $"Parcel status must be {string.Join(", ", All)}.");
        }

        if (s == Regular)
        {
            return (s, null);
        }

        var text = status is null && relatedNumbers is null ? currentRelated : Text.TextNormalize.NullIfBlank(relatedNumbers);
        if (text?.Length > MaxRelatedNumbersLength)
        {
            throw new Validation.DomainValidationException("PARCEL_RELATED_NUMBERS_TOO_LONG", $"The other OT / plot numbers are at most {MaxRelatedNumbersLength} characters.");
        }

        return (s, text);
    }
}
