using Nadlan.Core.Files;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Files;

/// <summary>
/// What is inside a photo or video (owner, 2026-10-31, N3): date taken, phone / camera, picture size, video length and
/// location, shown read-only in File details. The browser reads it (media-inspect.js, as Map drop does): right after an
/// upload from the local file, and for files stored before this from small slices served here. Stored once per file in
/// file_media_metadata, by a user who may change that file; anyone who may see the file sees it.
/// </summary>
public static class FileDetailsEndpoints
{
    // media-inspect.js reads at most 256 KB of a JPEG's head and an MP4's index (a phone's is well under this).
    private const int MaxSliceBytes = 8 * 1024 * 1024;

    public sealed record MediaDataDto(MediaMetadata? Metadata);

    private static bool IsMedia(string mimeType)
        => mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    public static void MapFileDetailsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files");

        group.MapGet("/{fileAttachmentId:long}/details", async (long fileAttachmentId, UserAccess me, AccessPolicy policy, IFileAttachmentStore files,
            FileService service, IMapDropStore media, IUserStore users, CancellationToken ct) =>
        {
            var file = await VisibleReadyFileAsync(fileAttachmentId, me, policy, files, service, ct);
            var m = await media.GetMetadataAsync(fileAttachmentId, ct);
            var uploader = file.UploadedByUserId is long uid ? await users.GetAsync(uid, ct) : null;
            var canSave = (await policy.FileTargetAsync(me, file.AttachedToType, file.AttachedToId, ct)).CanEdit;
            return Results.Ok(new
            {
                uploadedUtc = file.UploadedUtc,
                uploadedBy = uploader?.DisplayName, // staff only: File details is never shown to customers
                media = m is null ? null : new
                {
                    m.TakenLocal, m.CameraMake, m.CameraModel, m.Width, m.Height, m.DurationS,
                    m.Latitude, m.Longitude, m.RelAltitudeM, m.AbsAltitudeM,
                },
                // Not read yet: the browser may read it from the file (a photo or video) and, with the rights, store it.
                readFromFile = m is null && IsMedia(file.MimeType),
                canSave,
            });
        });

        // A slice of the stored file, for reading what is inside it. Same visibility as the file itself.
        group.MapGet("/{fileAttachmentId:long}/bytes", async (long fileAttachmentId, long start, long end, UserAccess me, AccessPolicy policy,
            IFileAttachmentStore files, FileService service, IObjectStorage storage, CancellationToken ct) =>
        {
            var file = await VisibleReadyFileAsync(fileAttachmentId, me, policy, files, service, ct);
            if (!IsMedia(file.MimeType))
            {
                throw new DomainValidationException("FILE_NOT_MEDIA", "Only photos and videos are read this way.");
            }

            if (start < 0 || end <= start || end - start > MaxSliceBytes)
            {
                throw new DomainValidationException("FILE_RANGE_INVALID", $"Ask for 1 byte to {MaxSliceBytes / 1048576} MB at a time.");
            }

            var bytes = await storage.ReadRangeAsync(file.StorageKey, start, (int)Math.Min(end - start, file.FileSize - start), ct)
                        ?? throw new EntityNotFoundException("File", fileAttachmentId);
            return Results.File(bytes, "application/octet-stream");
        });

        // What the browser read from the file. Kept only the first time (a Map drop reading, or one stored before, stays).
        group.MapPut("/{fileAttachmentId:long}/media-data", async (long fileAttachmentId, MediaDataDto dto, UserAccess me, AccessPolicy policy,
            IFileAttachmentStore files, FileService service, IMapDropStore media, CancellationToken ct) =>
        {
            var file = await VisibleReadyFileAsync(fileAttachmentId, me, policy, files, service, ct);
            if (!(await policy.FileTargetAsync(me, file.AttachedToType, file.AttachedToId, ct)).CanEdit)
            {
                throw new ForbiddenException("FILE_UPLOAD_FORBIDDEN", "You may not add or change files here.");
            }

            if (!IsMedia(file.MimeType))
            {
                return Results.NoContent();
            }

            if (await media.GetMetadataAsync(fileAttachmentId, ct) is null)
            {
                await media.UpsertMetadataAsync(fileAttachmentId, MapDropService.Clean(dto.Metadata ?? new MediaMetadata()), ct);
            }

            return Results.NoContent();
        });
    }

    private static async Task<FileAttachment> VisibleReadyFileAsync(long fileAttachmentId, UserAccess me, AccessPolicy policy, IFileAttachmentStore files,
        FileService service, CancellationToken ct)
    {
        var file = await files.GetAsync(fileAttachmentId, ct);
        if (file is not { UploadStatus: FileUploadStatus.Ready }
            || !(await policy.FileTargetAsync(me, file.AttachedToType, file.AttachedToId, ct)).CanView
            || !me.CanSeeFileCategory(await service.CategoryOfAsync(file.FileTypeId, ct)))
        {
            throw new EntityNotFoundException("File", fileAttachmentId);
        }

        return file;
    }
}
