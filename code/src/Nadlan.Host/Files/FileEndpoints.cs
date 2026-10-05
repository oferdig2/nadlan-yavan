using Nadlan.Core.Files;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Files;

/// <summary>
/// File API. Bytes never pass through here: the browser gets presigned part URLs, PUTs parts straight to S3,
/// then reports the part ETags so we can complete the upload.
/// Rights come from the entity the file belongs to; file categories (Legal, Engineering, ...) filter lists and URLs.
/// </summary>
public static class FileEndpoints
{
    public sealed record StartUploadDto(string? AttachedToType, long AttachedToId, int FileTypeId, string? FileName, string? MimeType, long FileSize);

    public sealed record PartNumbersDto(int[]? PartNumbers);

    public sealed record CompleteDto(UploadedPart[]? Parts);

    public sealed record MetadataDto(int FileTypeId, string? Caption, string? Notes, int? SortOrder);

    public static void MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files");

        // Files of one entity, only in the categories the caller may see (Scenario 23: hidden files are not even listed).
        group.MapGet("/", async (string? attachedToType, long attachedToId, UserAccess me, AccessPolicy policy,
            IFileAttachmentStore files, IFileUrlProvider urls, CancellationToken ct) =>
        {
            var type = FileTargetTypes.Normalize(attachedToType)
                ?? throw new DomainValidationException("FILE_TARGET_INVALID", "Unknown attachedToType.");
            await policy.RequireFileTargetViewAsync(me, type, attachedToId, ct);
            var items = await files.ListReadyAsync(type, attachedToId, ct);
            return Results.Ok(items.Where(f => me.CanSeeFileCategory(f.Category)).Select(f => ToDto(f, urls)));
        });

        // What the Files dialog may offer for this entity.
        group.MapGet("/rights", async (string? attachedToType, long attachedToId, UserAccess me, AccessPolicy policy, CancellationToken ct) =>
        {
            var type = FileTargetTypes.Normalize(attachedToType)
                ?? throw new DomainValidationException("FILE_TARGET_INVALID", "Unknown attachedToType.");
            var rights = await policy.RequireFileTargetViewAsync(me, type, attachedToId, ct);
            return Results.Ok(new { canUpload = rights.CanEdit, categories = me.VisibleFileCategories });
        });

        // Opens the file in a new tab / as an <img src>: short-lived signed URL behind a stable app URL.
        group.MapGet("/{fileAttachmentId:long}/content", async (long fileAttachmentId, UserAccess me, AccessPolicy policy,
            IFileAttachmentStore files, FileService service, IFileUrlProvider urls, CancellationToken ct) =>
        {
            var file = await files.GetAsync(fileAttachmentId, ct);
            if (file is not { UploadStatus: FileUploadStatus.Ready }
                || !(await policy.FileTargetAsync(me, file.AttachedToType, file.AttachedToId, ct)).CanView
                || !me.CanSeeFileCategory(await service.CategoryOfAsync(file.FileTypeId, ct)))
            {
                return Results.NotFound(new { error = "FILE_NOT_FOUND", message = "File not found." });
            }

            var url = urls.GetUrl(file.StorageKey);
            return url.Length == 0
                ? Results.Json(new { error = "STORAGE_NOT_CONFIGURED", message = "File storage is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Redirect(url);
        });

        group.MapPut("/{fileAttachmentId:long}", async (long fileAttachmentId, MetadataDto dto, UserAccess me, AccessPolicy policy,
            IFileAttachmentStore files, FileService service, CancellationToken ct) =>
        {
            var file = await GetVisibleFileAsync(fileAttachmentId, me, policy, files, service, ct);
            await policy.RequireFileChangeAsync(me, file.AttachedToType, file.AttachedToId, file.UploadedByUserId, await service.CategoryOfAsync(file.FileTypeId, ct), ct);
            if (dto.FileTypeId != file.FileTypeId && !me.CanSeeFileCategory(await service.CategoryOfAsync(dto.FileTypeId, ct)))
            {
                throw new ForbiddenException("FILE_CATEGORY_FORBIDDEN", "You may not move a file into that category.");
            }

            await service.UpdateMetadataAsync(fileAttachmentId, dto.FileTypeId, dto.Caption, dto.Notes, dto.SortOrder, ct);
            return Results.Ok(new { fileAttachmentId });
        });

        group.MapDelete("/{fileAttachmentId:long}", async (long fileAttachmentId, UserAccess me, AccessPolicy policy,
            IFileAttachmentStore files, FileService service, CancellationToken ct) =>
        {
            var file = await GetVisibleFileAsync(fileAttachmentId, me, policy, files, service, ct);
            await policy.RequireFileChangeAsync(me, file.AttachedToType, file.AttachedToId, file.UploadedByUserId, await service.CategoryOfAsync(file.FileTypeId, ct), ct);
            await service.DeleteAsync(fileAttachmentId, ct);
            return Results.NoContent();
        });

        // The preview image the browser made right after uploading a photo/video (raw image/jpeg body, small).
        group.MapPost("/{fileAttachmentId:long}/thumbnail", async (long fileAttachmentId, HttpRequest request, UserAccess me, AccessPolicy policy,
            IFileAttachmentStore files, FileService service, CancellationToken ct) =>
        {
            var file = await GetVisibleFileAsync(fileAttachmentId, me, policy, files, service, ct);
            await policy.RequireFileChangeAsync(me, file.AttachedToType, file.AttachedToId, file.UploadedByUserId, await service.CategoryOfAsync(file.FileTypeId, ct), ct);
            if (request.ContentLength is > FileService.MaxThumbnailBytes)
            {
                throw new DomainValidationException("THUMBNAIL_INVALID", $"The preview must be at most {FileService.MaxThumbnailBytes / 1024} KB.");
            }

            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > FileService.MaxThumbnailBytes)
                {
                    throw new DomainValidationException("THUMBNAIL_INVALID", $"The preview must be at most {FileService.MaxThumbnailBytes / 1024} KB.");
                }

                body.Write(buffer, 0, read);
            }

            await service.SaveThumbnailAsync(file, body.ToArray(), ct);
            return Results.NoContent();
        });

        var uploads = group.MapGroup("/uploads");

        uploads.MapPost("/", async (StartUploadDto dto, UserAccess me, AccessPolicy policy, FileService service, CancellationToken ct) =>
        {
            await policy.RequireFileUploadAsync(me, dto.AttachedToType ?? "", dto.AttachedToId, await service.CategoryOfAsync(dto.FileTypeId, ct), ct);
            return Results.Ok(await service.StartUploadAsync(new StartUploadRequest(
                dto.AttachedToType ?? "", dto.AttachedToId, dto.FileTypeId, dto.FileName ?? "", dto.MimeType, dto.FileSize, me.UserId), ct));
        });

        // The rest of an upload session belongs to whoever started it.
        uploads.MapPost("/{fileAttachmentId:long}/part-urls", async (long fileAttachmentId, PartNumbersDto dto, UserAccess me,
            IFileAttachmentStore files, FileService service, CancellationToken ct) =>
        {
            await RequireOwnUploadAsync(fileAttachmentId, me, files, ct);
            return Results.Ok(await service.GetPartUrlsAsync(fileAttachmentId, dto.PartNumbers ?? Array.Empty<int>(), ct));
        });

        uploads.MapGet("/{fileAttachmentId:long}/parts", async (long fileAttachmentId, UserAccess me, IFileAttachmentStore files,
            FileService service, CancellationToken ct) =>
        {
            await RequireOwnUploadAsync(fileAttachmentId, me, files, ct);
            return Results.Ok(await service.ListUploadedPartsAsync(fileAttachmentId, ct));
        });

        uploads.MapPost("/{fileAttachmentId:long}/complete", async (long fileAttachmentId, CompleteDto dto, UserAccess me,
            IFileAttachmentStore files, FileService service, CancellationToken ct) =>
        {
            await RequireOwnUploadAsync(fileAttachmentId, me, files, ct);
            var file = await service.CompleteUploadAsync(fileAttachmentId, dto.Parts ?? Array.Empty<UploadedPart>(), ct);
            return Results.Ok(new { file.FileAttachmentId });
        });

        uploads.MapDelete("/{fileAttachmentId:long}", async (long fileAttachmentId, UserAccess me, IFileAttachmentStore files,
            FileService service, CancellationToken ct) =>
        {
            await RequireOwnUploadAsync(fileAttachmentId, me, files, ct);
            await service.AbortUploadAsync(fileAttachmentId, ct);
            return Results.NoContent();
        });
    }

    /// <summary>The file, if the caller may see its entity and its category; otherwise "not found".</summary>
    private static async Task<FileAttachment> GetVisibleFileAsync(long fileAttachmentId, UserAccess me, AccessPolicy policy,
        IFileAttachmentStore files, FileService service, CancellationToken ct)
    {
        var file = await files.GetAsync(fileAttachmentId, ct);
        if (file is null
            || !(await policy.FileTargetAsync(me, file.AttachedToType, file.AttachedToId, ct)).CanView
            || !me.CanSeeFileCategory(await service.CategoryOfAsync(file.FileTypeId, ct)))
        {
            throw new EntityNotFoundException("File", fileAttachmentId);
        }

        return file;
    }

    private static async Task RequireOwnUploadAsync(long fileAttachmentId, UserAccess me, IFileAttachmentStore files, CancellationToken ct)
    {
        var file = await files.GetAsync(fileAttachmentId, ct);
        if (file is null || (file.UploadedByUserId != me.UserId && !me.IsAdmin))
        {
            throw new EntityNotFoundException("File", fileAttachmentId);
        }
    }

    private static object ToDto(FileListItem f, IFileUrlProvider urls) => new
    {
        fileAttachmentId = f.FileAttachmentId,
        fileTypeId = f.FileTypeId,
        fileTypeCode = f.FileTypeCode,
        fileTypeName = f.FileTypeName,
        category = f.Category,
        attachedToType = f.AttachedToType,
        attachedToId = f.AttachedToId,
        originalFileName = f.OriginalFileName,
        mimeType = f.MimeType,
        fileSize = f.FileSize,
        caption = f.Caption,
        notes = f.Notes,
        sortOrder = f.SortOrder,
        uploadedUtc = f.UploadedUtc,
        url = urls.GetUrl(f.StorageKey), // direct, signed, expiring - for the viewer and downloads
        thumbUrl = f.HasThumbnail ? urls.GetUrl(FileAttachment.ThumbnailKey(f.StorageKey)) : null, // small preview for cards
    };
}
