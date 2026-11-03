using System.Globalization;
using Nadlan.Core.Files;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Work;

/// <summary>
/// Pictures and videos of a work (owner, 2026-10-31): the contractor uploads them to the work, not to the Parcel - the
/// same direct-to-S3 multipart flow as the Parcel files (copied from <see cref="FileService"/>, which belongs to those
/// files and their rules), into the module's own table. They appear nowhere else until staff "Copy to parcel": then the
/// Parcel gets its own copy (Photo / Video, Marketing), the work keeps its proof. Also the daily clean-up.
/// </summary>
public sealed class WorkMediaService
{
    private const int MaxParts = 10_000;
    private const long MinPartBytes = 5L * 1024 * 1024;
    private const int MaxPartUrlsPerCall = 100;

    /// <summary>The track is kept this long (owner, 2026-11-01).</summary>
    public static readonly TimeSpan KeepLocationsFor = TimeSpan.FromDays(90);

    /// <summary>An upload still unfinished after this is abandoned (tab closed): the daily job removes it.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(1);

    /// <summary>What the contractor's page shows of a Parcel's own files - the presentation's rule (PresentationEndpoints).</summary>
    public const string CustomerCategory = "Marketing";

    public static readonly IReadOnlySet<string> CustomerMediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif", "image/avif",
        "video/mp4", "video/webm", "video/quicktime",
    };

    private readonly IWorkStore _store;
    private readonly IObjectStorage _storage;
    private readonly FileStorageSettings _settings;
    private readonly IFileAttachmentStore _files;
    private readonly IMapDropStore _mediaData;
    private readonly AccessPolicy _policy;
    private readonly TimeProvider _clock;

    public WorkMediaService(IWorkStore store, IObjectStorage storage, FileStorageSettings settings, IFileAttachmentStore files, IMapDropStore mediaData,
        AccessPolicy policy, TimeProvider? clock = null)
    {
        _store = store;
        _storage = storage;
        _settings = settings;
        _files = files;
        _mediaData = mediaData;
        _policy = policy;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ---- the contractor's uploads -------------------------------------------------------------------------------

    /// <summary>Pictures go on a work once its price is approved, until it is paid.</summary>
    public static bool TakesPictures(string status) => status is WorkStatuses.Approved or WorkStatuses.InWork or WorkStatuses.Done;

    public async Task<UploadSession> StartUploadAsync(WorkRequest r, long workItemId, string? fileName, string? mimeType, long fileSize,
        UploadPosition? position, CancellationToken ct = default)
    {
        EnsureConfigured();
        var work = await _store.GetItemAsync(r.WorkRequestId, workItemId, ct)
                   ?? throw new WorkConflictException("WORK_NOT_FOUND", "This work isn't on your list (any more). Reload the page.");
        if (!TakesPictures(work.Status))
        {
            throw new WorkConflictException("WORK_NO_PICTURES_YET", "Pictures can be added once the price is approved.");
        }

        var mime = (mimeType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (!(mime.StartsWith("image/", StringComparison.Ordinal) || mime.StartsWith("video/", StringComparison.Ordinal))
            || mime is "image/svg+xml")
        {
            throw new DomainValidationException("WORK_FILE_TYPE", "Only photos and videos can be added to a work.");
        }

        if (fileSize <= 0)
        {
            throw new DomainValidationException("FILE_EMPTY", "The file is empty.");
        }

        if (fileSize > _settings.MaxFileSizeBytes)
        {
            throw new DomainValidationException("FILE_TOO_LARGE", $"Files can be at most {_settings.MaxFileSizeBytes / (1024 * 1024 * 1024)} GB.");
        }

        var name = FileService.CleanFileName(fileName);
        var key = $"work/{r.WorkRequestId}/{workItemId}/{Guid.NewGuid():N}{FileService.KeyExtension(name)}";
        var uploadId = await _storage.StartMultipartUploadAsync(key, mime, FileService.ContentDisposition(name, mime), ct);
        var p = CleanPosition(position);
        var id = await _store.InsertWorkFileAsync(new WorkFile
        {
            WorkItemId = workItemId,
            StorageKey = key,
            OriginalName = name,
            MimeType = mime,
            FileSize = fileSize,
            S3UploadId = uploadId,
            UploadLatitude = p?.Latitude,
            UploadLongitude = p?.Longitude,
            UploadAccuracyM = p?.AccuracyM,
        }, ct);
        var partSize = PartSizeFor(fileSize);
        return new UploadSession(id, partSize, (int)((fileSize + partSize - 1) / partSize));
    }

    public async Task<IReadOnlyList<PartUrl>> PartUrlsAsync(WorkRequest r, long workFileId, IReadOnlyList<int>? partNumbers, CancellationToken ct = default)
    {
        var file = await PendingAsync(r, workFileId, ct);
        var partCount = (int)((file.FileSize + PartSizeFor(file.FileSize) - 1) / PartSizeFor(file.FileSize));
        var numbers = partNumbers ?? Array.Empty<int>();
        if (numbers.Count is 0 or > MaxPartUrlsPerCall || numbers.Any(n => n < 1 || n > partCount))
        {
            throw new DomainValidationException("FILE_PARTS_INVALID", $"Ask for 1-{MaxPartUrlsPerCall} part numbers between 1 and {partCount}.");
        }

        return numbers.Distinct().Select(n => new PartUrl(n, _storage.GetPartUploadUrl(file.StorageKey, file.S3UploadId!, n, _settings.PartUrlLifetime))).ToList();
    }

    public async Task<WorkFile> CompleteAsync(WorkRequest r, long workFileId, IReadOnlyList<UploadedPart>? parts, CancellationToken ct = default)
    {
        var file = await PendingAsync(r, workFileId, ct);
        if (parts is null || parts.Count == 0)
        {
            throw new DomainValidationException("FILE_PARTS_MISSING", "No uploaded parts were reported.");
        }

        await _storage.CompleteMultipartUploadAsync(file.StorageKey, file.S3UploadId!, parts.OrderBy(p => p.PartNumber).ToList(), ct);
        var size = await _storage.GetObjectSizeAsync(file.StorageKey, ct);
        if (size != file.FileSize)
        {
            if (await _store.DeletePendingWorkFileAsync(workFileId, ct))
            {
                await _storage.DeleteObjectAsync(file.StorageKey, ct);
            }

            throw new DomainValidationException("FILE_SIZE_MISMATCH", "The upload was incomplete. Please upload again.");
        }

        if (await _store.MarkWorkFileReadyAsync(workFileId, ct))
        {
            await _store.AddEventAsync(new NewWorkEvent(r.WorkRequestId, WorkActorKinds.Contractor, null, WorkActions.FileUploaded,
                $"Parcel {file.RegistryId}: {file.WorkTypeName} - {(file.MimeType.StartsWith("video/", StringComparison.Ordinal) ? "video" : "photo")} {file.OriginalName} uploaded.",
                file.ParcelId, file.WorkItemId), ct);
        }

        return file with { UploadStatus = FileUploadStatus.Ready, S3UploadId = null };
    }

    /// <summary>Cancel: the S3 upload is aborted and the row forgotten (unless it completed meanwhile).</summary>
    public async Task AbortAsync(WorkRequest r, long workFileId, CancellationToken ct = default)
    {
        var file = await PendingAsync(r, workFileId, ct);
        await _storage.AbortMultipartUploadAsync(file.StorageKey, file.S3UploadId!, ct);
        await _store.DeletePendingWorkFileAsync(workFileId, ct);
    }

    /// <summary>The small JPEG the phone made (cards show it instead of the original).</summary>
    public async Task SaveThumbnailAsync(WorkRequest r, long workFileId, byte[] jpeg, CancellationToken ct = default)
    {
        EnsureConfigured();
        var file = await ReadyAsync(r, workFileId, ct);
        if (jpeg.Length is 0 or > FileService.MaxThumbnailBytes || jpeg.Length < 3 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[2] != 0xFF)
        {
            throw new DomainValidationException("THUMBNAIL_INVALID", $"The preview must be a JPEG of at most {FileService.MaxThumbnailBytes / 1024} KB.");
        }

        await _storage.PutObjectAsync(FileAttachment.ThumbnailKey(file.StorageKey), jpeg, "image/jpeg", ct);
        await _store.SetWorkFileThumbnailAsync(workFileId, ct);
    }

    /// <summary>What the phone read from inside the file (media-inspect.js), checked like Map drop's (0, 0 = no place).</summary>
    public async Task SaveMediaDataAsync(WorkRequest r, long workFileId, MediaMetadata? metadata, CancellationToken ct = default)
    {
        await ReadyAsync(r, workFileId, ct);
        var m = MapDropService.Clean(metadata ?? new MediaMetadata());
        await _store.SetWorkFileMediaAsync(workFileId, new WorkFile
        {
            TakenLocal = m.TakenLocal, CameraMake = m.CameraMake, CameraModel = m.CameraModel, Latitude = m.Latitude, Longitude = m.Longitude,
            Width = m.Width, Height = m.Height, DurationS = m.DurationS,
        }, ct);
    }

    // ---- staff: Copy to parcel ----------------------------------------------------------------------------------

    /// <summary>
    /// Copies a work picture into the Parcel's own files (Photo / Video, Marketing - for presentations and marketing):
    /// the bytes and the preview server side, the file's data with it. The work keeps its own. Once per picture.
    /// Needs the right to add files to that Parcel.
    /// </summary>
    public async Task<long> CopyToParcelAsync(UserAccess me, long workRequestId, long workFileId, CancellationToken ct = default)
    {
        WorkService.Require(me);
        EnsureConfigured();
        var file = await _store.GetWorkFileAsync(workRequestId, workFileId, ct) ?? throw new EntityNotFoundException("Picture", workFileId);
        await _policy.RequireParcelViewAsync(me, file.ParcelId, ct);
        if (file.UploadStatus != FileUploadStatus.Ready)
        {
            throw new WorkConflictException("WORK_FILE_NOT_READY", "This picture is still uploading.");
        }

        if (file.CopiedToFileAttachmentId is not null)
        {
            throw new WorkConflictException("WORK_FILE_COPIED", "This picture is already in the parcel's files.");
        }

        var video = file.MimeType.StartsWith("video/", StringComparison.Ordinal);
        var typeCode = video ? "VIDEO" : "PHOTO";
        var type = (await _files.ListTypesAsync(ct)).FirstOrDefault(t => t.Code == typeCode && t.IsActive)
                   ?? throw new DomainValidationException("FILE_TYPE_REQUIRED", $"The file type {typeCode} isn't on the list (any more).");
        await _policy.RequireFileUploadAsync(me, FileTargetTypes.Parcel, file.ParcelId, type.Category, ct);

        // The key as FileService gives Parcel files: parcel/{id}/{guid}{.ext}.
        var key = $"parcel/{file.ParcelId}/{Guid.NewGuid():N}{FileService.KeyExtension(file.OriginalName)}";
        await _storage.CopyObjectAsync(file.StorageKey, key, ct);
        if (file.HasThumbnail)
        {
            await _storage.CopyObjectAsync(FileAttachment.ThumbnailKey(file.StorageKey), FileAttachment.ThumbnailKey(key), ct);
        }

        var id = await _files.InsertPendingAsync(new FileAttachment
        {
            FileTypeId = type.Id,
            AttachedToType = FileTargetTypes.Parcel,
            AttachedToId = file.ParcelId,
            StorageKey = key,
            OriginalFileName = file.OriginalName,
            MimeType = file.MimeType,
            FileSize = file.FileSize,
            UploadStatus = FileUploadStatus.Pending,
            UploadedByUserId = me.UserId,
        }, ct);
        await _files.MarkReadyAsync(id, ct);
        await _files.UpdateMetadataAsync(id, type.Id, null,
            $"From work request #{workRequestId}: {file.WorkTypeName}, uploaded by the contractor {file.UploadedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.",
            null, ct);
        if (file.HasThumbnail)
        {
            await _files.SetHasThumbnailAsync(id, ct);
        }

        if (file.TakenLocal is not null || file.CameraMake is not null || file.Latitude is not null || file.Width is not null || file.DurationS is not null)
        {
            await _mediaData.UpsertMetadataAsync(id, new MediaMetadata
            {
                TakenLocal = file.TakenLocal, CameraMake = file.CameraMake, CameraModel = file.CameraModel, Latitude = file.Latitude,
                Longitude = file.Longitude, Width = file.Width, Height = file.Height, DurationS = file.DurationS,
            }, ct);
        }

        if (!await _store.MarkWorkFileCopiedAsync(workFileId, id, ct))
        {
            // Copied by someone else at the same moment: keep theirs, remove this second copy.
            await _files.DeleteAsync(id, ct);
            await _storage.DeleteObjectAsync(key, ct);
            throw new WorkConflictException("WORK_FILE_COPIED", "This picture is already in the parcel's files.");
        }

        await _store.AddEventAsync(new NewWorkEvent(workRequestId, WorkActorKinds.Staff, me.UserId, WorkActions.CopiedToParcel,
            $"Parcel {file.RegistryId}: {file.WorkTypeName} - {file.OriginalName} copied to the parcel's files ({type.Name}).", file.ParcelId, file.WorkItemId), ct);
        return id;
    }

    // ---- the daily job ------------------------------------------------------------------------------------------

    /// <summary>Deletes track paths untouched for 90 days, and work uploads abandoned for a day. Returns both counts.</summary>
    public async Task<(int Locations, int Uploads)> SweepAsync(CancellationToken ct = default)
    {
        var locations = await _store.PurgeLocationsAsync(Now - KeepLocationsFor, ct);
        var uploads = 0;
        if (_storage.IsConfigured)
        {
            foreach (var file in await _store.ListStalePendingWorkFilesAsync(Now - AbandonedAfter, 500, ct))
            {
                // S3 may hold the whole file (completed, then the Ready step failed): keep it.
                if (await _storage.GetObjectSizeAsync(file.StorageKey, ct) == file.FileSize)
                {
                    await _store.MarkWorkFileReadyAsync(file.WorkFileId, ct);
                    continue;
                }

                if (file.S3UploadId is not null)
                {
                    await _storage.AbortMultipartUploadAsync(file.StorageKey, file.S3UploadId, ct);
                }

                if (await _store.DeletePendingWorkFileAsync(file.WorkFileId, ct))
                {
                    await _storage.DeleteObjectAsync(file.StorageKey, ct);
                    uploads++;
                }
            }
        }

        return (locations, uploads);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>A position that is a place: within range, not 0, 0 (some phones write that for "none").</summary>
    internal static UploadPosition? CleanPosition(UploadPosition? p)
        => p is null || double.IsNaN(p.Latitude) || double.IsNaN(p.Longitude) || Math.Abs(p.Latitude) > 90 || Math.Abs(p.Longitude) > 180
           || (p.Latitude == 0 && p.Longitude == 0)
            ? null
            : p with { AccuracyM = p.AccuracyM is decimal a && a >= 0 && a < 1_000_000 ? decimal.Round(a, 1) : null };

    private async Task<WorkFile> PendingAsync(WorkRequest r, long workFileId, CancellationToken ct)
    {
        EnsureConfigured();
        var file = await _store.GetWorkFileAsync(r.WorkRequestId, workFileId, ct) ?? throw new EntityNotFoundException("Upload", workFileId);
        if (file.UploadStatus != FileUploadStatus.Pending || file.S3UploadId is null)
        {
            throw new DomainValidationException("FILE_NOT_UPLOADING", "This file is not being uploaded.");
        }

        return file;
    }

    private async Task<WorkFile> ReadyAsync(WorkRequest r, long workFileId, CancellationToken ct)
    {
        var file = await _store.GetWorkFileAsync(r.WorkRequestId, workFileId, ct) ?? throw new EntityNotFoundException("Upload", workFileId);
        return file.UploadStatus == FileUploadStatus.Ready ? file
            : throw new DomainValidationException("FILE_NOT_READY", "The file hasn't finished uploading.");
    }

    private long PartSizeFor(long fileSize)
    {
        var size = Math.Max(_settings.PartSizeBytes, MinPartBytes);
        var needed = (fileSize + MaxParts - 1) / MaxParts;
        if (needed > size)
        {
            const long mib = 1024 * 1024;
            size = (needed + mib - 1) / mib * mib;
        }

        return size;
    }

    private void EnsureConfigured()
    {
        if (!_storage.IsConfigured)
        {
            throw new DomainValidationException("STORAGE_NOT_CONFIGURED", "File storage is not configured (Nadlan:Storage:Bucket in app_config).");
        }
    }
}
