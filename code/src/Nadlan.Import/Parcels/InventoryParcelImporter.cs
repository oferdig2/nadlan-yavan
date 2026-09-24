using System.Text.Json;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Import;
using Nadlan.Core.Parcels;
using Nadlan.Core.Text;
using Nadlan.Import.Legacy;

namespace Nadlan.Import.Parcels;

public sealed record ParcelImportResult(
    long ImportBatchId,
    int ParcelsCreated,
    int ParcelsAlreadyExisting,
    int ParcelsInvalidGeometry,
    IReadOnlyList<ParcelOverlap> Overlaps);

/// <summary>Writes a <see cref="ParcelImportPlan"/> to the database with full row-level traceability.</summary>
public sealed class InventoryParcelImporter
{
    public const string Source = "Airtable";
    public const string TargetEntityType = "Parcel";

    /// <summary>Overlaps smaller than this are digitising noise along shared borders, not real conflicts.</summary>
    public const double MinOverlapSqm = ParcelService.MinOverlapSqm;

    private readonly IParcelStore _parcels;
    private readonly IGeographicAreaStore _areas;
    private readonly ICountryStore _countries;
    private readonly IImportStore _imports;

    public InventoryParcelImporter(IParcelStore parcels, IGeographicAreaStore areas, ICountryStore countries, IImportStore imports)
    {
        _parcels = parcels;
        _areas = areas;
        _countries = countries;
        _imports = imports;
    }

    public async Task<ParcelImportResult> ImportAsync(ParcelImportPlan plan, string sourceFile, CancellationToken ct = default)
    {
        var countryId = await _countries.GetIdByCodeAsync("GR", ct)
            ?? throw new InvalidOperationException("Country GR is missing; has the schema been migrated?");
        var areaIds = await EnsureAreasAsync(countryId, plan.Parcels.Select(p => p.Area), ct);

        var batchId = await _imports.StartBatchAsync(Source, Path.GetFileName(sourceFile), importedByUserId: null, ct);
        int created = 0, existing = 0, invalid = 0;
        try
        {
            foreach (var planned in plan.Parcels)
            {
                var found = await _parcels.GetByRegistryIdAsync(countryId, planned.RegistryId, ct);
                if (found is not null)
                {
                    existing++;
                    foreach (var row in planned.AllRows)
                    {
                        await RecordAsync(batchId, row, found.ParcelId, ImportRecordStatus.Linked, "Parcel already existed (re-run).", ct);
                    }

                    continue;
                }

                if (!await _parcels.IsValidGeometryAsync(planned.Geometry, ct))
                {
                    invalid++;
                    foreach (var row in planned.AllRows)
                    {
                        await RecordAsync(batchId, row, null, ImportRecordStatus.NeedsReview, "Polygon is not valid (self-intersecting or degenerate).", ct);
                    }

                    continue;
                }

                var parcelId = await _parcels.InsertAsync(new Parcel
                {
                    CountryId = countryId,
                    RegistryId = planned.RegistryId,
                    RegistryIdIsProvisional = true,
                    GeographicAreaId = areaIds[planned.Area.Code],
                    Geometry = planned.Geometry,
                    OfficialAreaSqm = planned.OfficialAreaSqm,
                    OT = planned.OT,
                    OTExt = TextNormalize.NullIfBlank(planned.OTExt),
                    PlotNumber = planned.Plot,
                    PlotExt = TextNormalize.NullIfBlank(planned.PlotExt),
                    Inclination = planned.Inclination,
                    BuildFactor = planned.BuildFactor,
                    Notes = "Imported from legacy Airtable Inventory. Real KAEK unknown - registry id is provisional.",
                }, ct);
                created++;

                var warningText = planned.Warnings.Count > 0 ? string.Join(" | ", planned.Warnings) : null;
                await RecordAsync(batchId, planned.SourceRow, parcelId,
                    warningText is null ? ImportRecordStatus.Created : ImportRecordStatus.Warning,
                    warningText ?? "Created Parcel with provisional KAEK.", ct);
                foreach (var row in planned.LinkedRows)
                {
                    await RecordAsync(batchId, row, parcelId, ImportRecordStatus.Linked, "Same land as another legacy row.", ct);
                }
            }

            foreach (var rejected in plan.Rejected)
            {
                await RecordAsync(batchId, rejected.Row, null, ImportRecordStatus.NeedsReview, rejected.Reason, ct);
            }

            var overlaps = await _parcels.FindOverlapsAsync(MinOverlapSqm, ct);
            var summary = $"rows={plan.TotalRows}; withoutCoordinates={plan.RowsWithoutCoordinates} (not imported); " +
                          $"parcelsCreated={created}; alreadyExisting={existing}; invalidGeometry={invalid}; " +
                          $"rejectedRows={plan.Rejected.Count}; overlaps={overlaps.Count}";
            await _imports.CompleteBatchAsync(batchId, ImportBatchStatus.Completed, summary, ct);
            return new ParcelImportResult(batchId, created, existing, invalid, overlaps);
        }
        catch (Exception ex)
        {
            await _imports.CompleteBatchAsync(batchId, ImportBatchStatus.Failed, ex.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task<Dictionary<string, int>> EnsureAreasAsync(int countryId, IEnumerable<AreaIdentity> areas, CancellationToken ct)
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in areas.DistinctBy(a => a.Code))
        {
            var found = await _areas.GetByCodeAsync(countryId, area.Code, ct);
            ids[area.Code] = found?.GeographicAreaId ?? await _areas.InsertAsync(countryId, area.Code, area.Name, ct);
        }

        return ids;
    }

    private Task RecordAsync(long batchId, LegacyInventoryRow row, long? parcelId, string status, string message, CancellationToken ct)
        => _imports.AddRecordAsync(new ImportRecord(
            batchId, LegacyInventoryRow.LegacyTable, row.LegacyRecordId, TargetEntityType, parcelId, status, message,
            JsonSerializer.Serialize(new
            {
                row.RowNumber, row.InventoryItem, row.Agent, row.IsDeleted, Area = row.Area?.Name,
                row.OT, row.OTExt, row.Plot, row.PlotExt, row.PlotSqm, row.BuildFactor, row.Inclination, row.KmlCoordinates,
            })), ct);

}
