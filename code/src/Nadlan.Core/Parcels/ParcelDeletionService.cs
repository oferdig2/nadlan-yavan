using Nadlan.Core.Activity;
using Nadlan.Core.Assets;
using Nadlan.Core.Files;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Parcels;

/// <summary>FilesLeftInStorage: storage keys whose bytes could not be removed (the rows are gone; the caller logs them).</summary>
public sealed record ParcelDeletionResult(string? RegistryId, int FilesDeleted, IReadOnlyList<string> FilesLeftInStorage);

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

        var files = new List<FileAttachment>();
        foreach (var item in await _files.ListReadyAsync(FileTargetTypes.Parcel, parcelId, ct))
        {
            if (await _files.GetAsync(item.FileAttachmentId, ct) is { } file)
            {
                files.Add(file);
            }
        }

        // The database first, in one locked transaction that re-checks for Assets (one may have been added since the
        // check above); only once the Parcel is really gone are its files' bytes removed.
        switch (await _parcels.DeleteAsync(parcelId, ct))
        {
            case ParcelDeleteOutcome.NotFound:
                throw new EntityNotFoundException("Parcel", parcelId);
            case ParcelDeleteOutcome.HasAssets:
                throw new DomainValidationException("PARCEL_HAS_ASSETS", $"An Asset was just added on Parcel {parcel.RegistryId}, so it can't be deleted.");
        }

        var leftInStorage = new List<string>();
        foreach (var file in files)
        {
            try
            {
                await _fileService.DeleteStoredObjectAsync(file, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                leftInStorage.Add($"{file.StorageKey} ({ex.Message})");
            }
        }

        await _activity.RecordAsync(new ActivityEntry("Parcel", parcelId, ActivityActions.ParcelDeleted,
            $"Parcel {parcel.RegistryId} deleted{(files.Count > 0 ? $" with {files.Count} file(s)" : "")}.",
            new { parcel.RegistryId, parcel.RegistryIdIsProvisional, parcel.GeographicAreaId, parcel.OfficialAreaSqm, geometry = parcel.Geometry.ToWkt() }), ct);
        return new ParcelDeletionResult(parcel.RegistryId, files.Count, leftInStorage);
    }
}
