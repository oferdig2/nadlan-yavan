using Nadlan.Core.Geo;
using Nadlan.Import.Legacy;

namespace Nadlan.Import.Parcels;

/// <summary>One Parcel to create, derived from one or more legacy rows that describe the same land.</summary>
public sealed class PlannedParcel
{
    public required string RegistryId { get; init; }
    public required AreaIdentity Area { get; init; }
    public required GeoPolygon Geometry { get; init; }
    public required string OT { get; init; }
    public required string OTExt { get; init; }
    public required string Plot { get; init; }
    public required string PlotExt { get; init; }
    public decimal? OfficialAreaSqm { get; init; }
    public decimal? BuildFactor { get; init; }
    public decimal? Inclination { get; init; }

    /// <summary>The row whose geometry was chosen. It "creates" the Parcel; the others link to it.</summary>
    public required LegacyInventoryRow SourceRow { get; init; }

    public required IReadOnlyList<LegacyInventoryRow> LinkedRows { get; init; }
    public List<string> Warnings { get; } = new();

    public IEnumerable<LegacyInventoryRow> AllRows => LinkedRows.Prepend(SourceRow);
}

/// <summary>A legacy row that could not become (part of) a Parcel. Goes to the review list.</summary>
public sealed record RejectedRow(LegacyInventoryRow Row, string Reason);

public sealed class ParcelImportPlan
{
    public required int TotalRows { get; init; }
    public required int RowsWithoutCoordinates { get; init; }
    public required IReadOnlyList<PlannedParcel> Parcels { get; init; }
    public required IReadOnlyList<RejectedRow> Rejected { get; init; }
}
