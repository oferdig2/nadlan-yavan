using Nadlan.Import.Csv;

namespace Nadlan.Import.Legacy;

/// <summary>The Parcel-relevant slice of one legacy Airtable Inventory row.</summary>
public sealed record LegacyInventoryRow
{
    public const string LegacyTable = "Inventory";

    public required int RowNumber { get; init; }

    /// <summary>
    /// Airtable exported no unique record id ("Property RecordID" is empty), so we combine the
    /// agent-property identifier with the row number of this export.
    /// </summary>
    public required string LegacyRecordId { get; init; }

    public required string InventoryItem { get; init; }
    public required string Agent { get; init; }
    public required bool IsDeleted { get; init; }
    public required AreaIdentity? Area { get; init; }
    public required string OT { get; init; }
    public required string OTExt { get; init; }
    public required string Plot { get; init; }
    public required string PlotExt { get; init; }
    public required string KmlCoordinates { get; init; }
    public required string PlotSqm { get; init; }
    public required string BuildFactor { get; init; }
    public required string Inclination { get; init; }

    public static IReadOnlyList<LegacyInventoryRow> ReadAll(CsvTable csv)
    {
        var rows = new List<LegacyInventoryRow>(csv.Rows.Count);
        for (var i = 0; i < csv.Rows.Count; i++)
        {
            var r = csv.Rows[i];
            var rowNumber = i + 1;
            var agentPropertyId = csv.Get(r, "Agent-Property Identifier");
            rows.Add(new LegacyInventoryRow
            {
                RowNumber = rowNumber,
                LegacyRecordId = $"{(agentPropertyId.Length > 0 ? agentPropertyId : csv.Get(r, "Inventory Item"))}#row{rowNumber}",
                InventoryItem = csv.Get(r, "Inventory Item"),
                Agent = csv.Get(r, "Agent"),
                IsDeleted = string.Equals(csv.Get(r, "Property Status"), "Deleted", StringComparison.OrdinalIgnoreCase),
                Area = LegacyAreas.Resolve(csv.Get(r, "geographic area")),
                OT = csv.Get(r, "OT"),
                OTExt = csv.Get(r, "OT Ext."),
                Plot = csv.Get(r, "Plot"),
                PlotExt = csv.Get(r, "Plot Ext."),
                KmlCoordinates = csv.Get(r, "KML coordinates"),
                PlotSqm = csv.Get(r, "Plot SQM"),
                BuildFactor = csv.Get(r, "Build. Factor"),
                Inclination = csv.Get(r, "Inclination ~"),
            });
        }

        return rows;
    }
}
