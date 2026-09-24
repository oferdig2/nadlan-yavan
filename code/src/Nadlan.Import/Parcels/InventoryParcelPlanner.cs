using System.Globalization;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Text;
using Nadlan.Import.Legacy;

namespace Nadlan.Import.Parcels;

/// <summary>
/// Turns legacy Inventory rows into a Parcel plan without touching the database.
///
/// Rules:
/// - Only rows with KML coordinates are considered (the rest are counted, not imported).
/// - One Parcel per area + OT + OT ext + Plot + Plot ext. Several rows for the same land (e.g. two agents) become
///   one Parcel; their Assets come later.
/// - The real KAEK is unknown, so each Parcel gets a provisional TMP- registry id.
/// - When rows for the same Parcel disagree on geometry, the geometry most rows agree on wins and the others are
///   flagged. Identical geometry under different OT/Plot keys is flagged too.
/// </summary>
public static class InventoryParcelPlanner
{
    public static ParcelImportPlan Plan(IReadOnlyList<LegacyInventoryRow> rows)
    {
        var withCoordinates = rows.Where(r => r.KmlCoordinates.Length > 0).ToList();
        var rejected = new List<RejectedRow>();
        var candidates = new List<(LegacyInventoryRow Row, GeoPolygon Polygon, IReadOnlyList<string> Warnings, string Key)>();

        foreach (var row in withCoordinates)
        {
            if (row.Area is null)
            {
                rejected.Add(new RejectedRow(row, "No geographic area; cannot build a provisional KAEK."));
                continue;
            }

            if (row.OT.Length == 0 || row.Plot.Length == 0)
            {
                rejected.Add(new RejectedRow(row, "OT or Plot is missing; cannot identify the Parcel."));
                continue;
            }

            var parsed = KmlCoordinates.ParseRing(row.KmlCoordinates);
            if (!parsed.Success)
            {
                rejected.Add(new RejectedRow(row, $"Unusable KML coordinates: {parsed.Error}"));
                continue;
            }

            var key = ProvisionalRegistryId.Create(row.Area.Code, row.OT, row.OTExt, row.Plot, row.PlotExt);
            candidates.Add((row, parsed.Polygon!, parsed.Warnings, key));
        }

        var parcels = new List<PlannedParcel>();
        foreach (var group in candidates.GroupBy(c => c.Key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();

            // Most-agreed geometry wins; ties prefer a non-deleted listing, then the earliest row.
            var byGeometry = members
                .GroupBy(m => GeometryKey(m.Polygon))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.All(m => m.Row.IsDeleted) ? 1 : 0)
                .ThenBy(g => g.Min(m => m.Row.RowNumber))
                .ToList();
            var chosen = byGeometry[0].OrderBy(m => m.Row.IsDeleted ? 1 : 0).ThenBy(m => m.Row.RowNumber).First();
            var rowsInOrder = members.Select(m => m.Row).OrderBy(r => r.RowNumber).ToList();

            var warnings = new List<string>();
            warnings.AddRange(chosen.Warnings);
            foreach (var other in byGeometry.Skip(1))
            {
                warnings.Add($"Geometry conflict: rows {string.Join(", ", other.Select(m => m.Row.RowNumber))} " +
                             $"have a different polygon; kept the one from row {chosen.Row.RowNumber}.");
            }

            if (members.All(m => m.Row.IsDeleted))
            {
                warnings.Add("Every legacy listing for this Parcel is marked Deleted.");
            }

            var sqm = PickAttribute(rowsInOrder, chosen.Row, r => r.PlotSqm, ParseDecimal, "Plot SQM", warnings);
            var buildFactor = PickAttribute(rowsInOrder, chosen.Row, r => r.BuildFactor, ParseDecimal, "Build factor", warnings);
            var inclination = PickAttribute(rowsInOrder, chosen.Row, r => r.Inclination, ParseInclination, "Inclination", warnings);
            if (inclination is > 100)
            {
                warnings.Add($"Inclination '{chosen.Row.Inclination}' is not a plausible percentage (maybe a range); left empty.");
                inclination = null;
            }

            var parcel = new PlannedParcel
            {
                RegistryId = group.Key,
                Area = chosen.Row.Area!,
                Geometry = chosen.Polygon,
                OT = chosen.Row.OT,
                OTExt = chosen.Row.OTExt,
                Plot = chosen.Row.Plot,
                PlotExt = chosen.Row.PlotExt,
                OfficialAreaSqm = sqm,
                BuildFactor = buildFactor,
                Inclination = inclination,
                SourceRow = chosen.Row,
                LinkedRows = rowsInOrder.Where(r => r != chosen.Row).ToList(),
            };
            parcel.Warnings.AddRange(warnings);
            parcels.Add(parcel);
        }

        FlagSharedGeometry(parcels);

        return new ParcelImportPlan
        {
            TotalRows = rows.Count,
            RowsWithoutCoordinates = rows.Count - withCoordinates.Count,
            Parcels = parcels,
            Rejected = rejected,
        };
    }

    private static void FlagSharedGeometry(List<PlannedParcel> parcels)
    {
        foreach (var shared in parcels.GroupBy(p => GeometryKey(p.Geometry)).Where(g => g.Count() > 1))
        {
            foreach (var parcel in shared)
            {
                var others = shared.Where(p => p != parcel).Select(p => p.RegistryId);
                parcel.Warnings.Add($"Identical polygon to {string.Join(", ", others)} (different OT/Plot) - likely a copy/paste error.");
            }
        }
    }

    /// <summary>Takes the value from the chosen row, falling back to the first row that has one. Notes disagreements.</summary>
    private static decimal? PickAttribute(
        IReadOnlyList<LegacyInventoryRow> rows, LegacyInventoryRow chosen,
        Func<LegacyInventoryRow, string> select, Func<string, decimal?> parse, string label, List<string> warnings)
    {
        var values = rows.Select(r => (Row: r, Value: parse(select(r)))).Where(x => x.Value.HasValue).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        var distinct = values.Select(v => v.Value!.Value).Distinct().ToList();
        if (distinct.Count > 1)
        {
            warnings.Add($"{label} differs between rows ({string.Join(" / ", distinct.Select(d => d.ToString(CultureInfo.InvariantCulture)))}).");
        }

        return values.FirstOrDefault(v => v.Row == chosen).Value ?? values[0].Value;
    }

    private static decimal? ParseDecimal(string text) => TextNormalize.ParseDecimal(text);

    private static decimal? ParseInclination(string text)
        => ParseDecimal(text.Replace("%", "").Replace("~", "").Trim());

    /// <summary>Geometry identity at ~1 cm precision so float noise doesn't hide equal polygons.</summary>
    private static string GeometryKey(GeoPolygon polygon)
        => string.Join(';', polygon.Exterior.Select(p =>
            $"{Math.Round(p.Lon, 7).ToString(CultureInfo.InvariantCulture)},{Math.Round(p.Lat, 7).ToString(CultureInfo.InvariantCulture)}"));
}
