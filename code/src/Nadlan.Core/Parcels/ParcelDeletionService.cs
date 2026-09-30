using Nadlan.Core.Activity;
using Nadlan.Core.Assets;
using Nadlan.Core.Files;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Parcels;

public sealed record ParcelDeletionResult(string? RegistryId, int FilesDeleted);

/// <summary>
/// Deletes a Parcel (Admin only - the endpoint checks). A Parcel an Asset stands on is refused: the Asset would lose its
/// ground. Files go with it (from S3 too), legal owners and object grants as well. The history keeps a
/// "Parcel deleted" entry with the KAEK and the polygon, so a mistake can be recreated by hand.
/// </summary>
public sealed class ParcelDeletionService
{
    private readonly IParcelStore _parcels;
    private readonly IAssetStore _assets;
    private readonly IFileAttachmentStore _files;
    private readonly FileService _fileService;
    private readonly IActivityLog _activity;

    public ParcelDeletionService(IParcelStore parcels, IAssetStore assets, IFileAttachmentStore files, FileService fileService, IActivityLog? activity = null)
    {
        _parcels = parcels;
        _assets = assets;
        _files = files;
        _fileService = fileService;
        _activity = activity ?? NullActivityLog.Instance;
    }

    /// <summary>What deleting would remove, for the confirmation dialog.</summary>
    public async Task<(int Assets, int Files)> PreviewAsync(long parcelId, CancellationToken ct = default)
    {
        _ = await _parcels.GetAsync(parcelId, ct) ?? throw new EntityNotFoundException("Parcel", parcelId);
        var assets = await _assets.ListByParcelAsync(parcelId, AccessScope.Everything, ct);
        var files = await _files.ListReadyAsync(FileTargetTypes.Parcel, parcelId, ct);
        return (assets.Select(a => a.AssetId).Distinct().Count(), files.Count);
    }

    public async Task<ParcelDeletionResult> DeleteAsync(long parcelId, CancellationToken ct = default)
    {
        var parcel = await _parcels.GetAsync(parcelId, ct) ?? throw new EntityNotFoundException("Parcel", parcelId);
        var assetIds = (await _assets.ListByParcelAsync(parcelId, AccessScope.Everything, ct)).Select(a => a.AssetId).Distinct().ToList();
        if (assetIds.Count > 0)
        {
            throw new DomainValidationException("PARCEL_HAS_ASSETS",
                $"Parcel {parcel.RegistryId} carries {assetIds.Count} Asset(s) ({string.Join(", ", assetIds.Select(id => "#" + id))}), so it can't be deleted.");
        }

        var files = await _files.ListReadyAsync(FileTargetTypes.Parcel, parcelId, ct);
        foreach (var file in files)
        {
            await _fileService.DeleteAsync(file.FileAttachmentId, ct); // object in S3, row, "file deleted" history
        }

        if (!await _parcels.DeleteAsync(parcelId, ct))
        {
            throw new EntityNotFoundException("Parcel", parcelId);
        }

        await _activity.RecordAsync(new ActivityEntry("Parcel", parcelId, ActivityActions.ParcelDeleted,
            $"Parcel {parcel.RegistryId} deleted{(files.Count > 0 ? $" with {files.Count} file(s)" : "")}.",
            new { parcel.RegistryId, parcel.RegistryIdIsProvisional, parcel.GeographicAreaId, parcel.OfficialAreaSqm, geometry = parcel.Geometry.ToWkt() }), ct);
        return new ParcelDeletionResult(parcel.RegistryId, files.Count);
    }
}
