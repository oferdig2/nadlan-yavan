using System.Text;

namespace Nadlan.Import.Parcels;

/// <summary>Human-readable report of a plan (and, after a real run, its database outcome).</summary>
public static class ParcelImportReport
{
    public static string Build(ParcelImportPlan plan, ParcelImportResult? result)
    {
        var sb = new StringBuilder();
        var rowsInParcels = plan.Parcels.Sum(p => p.AllRows.Count());

        sb.AppendLine("LEGACY INVENTORY -> PARCELS");
        sb.AppendLine(result is null ? "Mode: DRY RUN (nothing written)" : $"Mode: IMPORT  (import_batch_id = {result.ImportBatchId})");
        sb.AppendLine();
        sb.AppendLine($"Legacy rows ..................... {plan.TotalRows}");
        sb.AppendLine($"  without coordinates (skipped) . {plan.RowsWithoutCoordinates}");
        sb.AppendLine($"  mapped to a Parcel ............ {rowsInParcels}");
        sb.AppendLine($"  rejected (review list) ........ {plan.Rejected.Count}");
        sb.AppendLine($"Parcels planned ................. {plan.Parcels.Count}");
        sb.AppendLine($"  shared by several legacy rows . {plan.Parcels.Count(p => p.LinkedRows.Count > 0)}");
        sb.AppendLine($"  with warnings ................. {plan.Parcels.Count(p => p.Warnings.Count > 0)}");
        sb.AppendLine($"  from Deleted listings only .... {plan.Parcels.Count(p => p.AllRows.All(r => r.IsDeleted))}");

        if (result is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Parcels created ................. {result.ParcelsCreated}");
            sb.AppendLine($"Parcels already existing ........ {result.ParcelsAlreadyExisting}");
            sb.AppendLine($"Rejected by DB (invalid polygon)  {result.ParcelsInvalidGeometry}");
            sb.AppendLine($"Overlapping pairs (>= {InventoryParcelImporter.MinOverlapSqm} m2) .. {result.Overlaps.Count}");
        }

        sb.AppendLine();
        sb.AppendLine("PROVISIONAL KAEK: every imported Parcel has an invented registry id (TMP-<area>-OT<n>-P<n>).");
        sb.AppendLine("They are flagged registry_id_is_provisional = 1 and must be replaced by the real KAEK.");

        sb.AppendLine();
        sb.AppendLine("Areas:");
        foreach (var area in plan.Parcels.GroupBy(p => p.Area).OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"  {area.Key.Code}  {area.Key.Name,-22} {area.Count()} parcels");
        }

        if (plan.Rejected.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Rejected rows:");
            foreach (var r in plan.Rejected)
            {
                sb.AppendLine($"  row {r.Row.RowNumber,4}  {r.Row.InventoryItem,-18} {r.Reason}");
            }
        }

        var warned = plan.Parcels.Where(p => p.Warnings.Count > 0).ToList();
        if (warned.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Parcel warnings:");
            foreach (var p in warned)
            {
                foreach (var w in p.Warnings)
                {
                    sb.AppendLine($"  {p.RegistryId,-22} {w}");
                }
            }
        }

        if (result is { Overlaps.Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Overlapping Parcels (quality-control review, not proof of error):");
            foreach (var o in result.Overlaps)
            {
                sb.AppendLine($"  {o.RegistryIdA,-22} x {o.RegistryIdB,-22} {o.OverlapSqm,10:N1} m2");
            }
        }

        return sb.ToString();
    }
}
