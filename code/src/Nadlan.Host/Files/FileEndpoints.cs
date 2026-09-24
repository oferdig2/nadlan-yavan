using Nadlan.Core.Files;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Files;

/// <summary>
/// File API. Bytes never pass through here: the browser gets presigned part URLs, PUTs parts straight to S3,
/// then reports the part ETags so we can complete the upload.
/// TODO(auth slice): open for now; file-category permissions (Legal/Engineering/...) filter lists and URLs later.
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

        group.MapGet("/", async (string? attachedToType, long attachedToId, IFileAttachmentStore files, IFileUrlProvider urls, CancellationToken ct) =>
        {
            var type = FileTargetTypes.Normalize(attachedToType)
                ?? throw new DomainValidationException("FILE_TARGET_INVALID", "Unknown attachedToType.");
            var items = await files.ListReadyAsync(type, attachedToId, ct);
            return Results.Ok(items.Select(f => ToDto(f, urls)));
        });

        // Opens the file in a new tab / as an <img src>: short-lived signed URL behind a stable app URL.
        group.MapGet("/{fileAttachmentId:long}/content", async (long fileAttachmentId, IFileAttachmentStore files, IFileUrlProvider urls, CancellationToken ct) =>
        {
            var file = await files.GetAsync(fileAttachmentId, ct);
            return file is { UploadStatus: FileUploadStatus.Ready }
                ? Results.Redirect(urls.GetUrl(file.StorageKey))
                : Results.NotFound(new { error = "FILE_NOT_FOUND", message = "File not found." });
        });

        group.MapPut("/{fileAttachmentId:long}", async (long fileAttachmentId, MetadataDto dto, FileService service, CancellationToken ct) =>
        {
            await service.UpdateMetadataAsync(fileAttachmentId, dto.FileTypeId, dto.Caption, dto.Notes, dto.SortOrder, ct);
            return Results.Ok(new { fileAttachmentId });
        });

        group.MapDelete("/{fileAttachmentId:long}", async (long fileAttachmentId, FileService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(fileAttachmentId, ct);
            return Results.NoContent();
        });

        var uploads = group.MapGroup("/uploads");

        uploads.MapPost("/", async (StartUploadDto dto, FileService service, CancellationToken ct) =>
            Results.Ok(await service.StartUploadAsync(new StartUploadRequest(
                dto.AttachedToType ?? "", dto.AttachedToId, dto.FileTypeId, dto.FileName ?? "", dto.MimeType, dto.FileSize), ct)));

        uploads.MapPost("/{fileAttachmentId:long}/part-urls", async (long fileAttachmentId, PartNumbersDto dto, FileService service, CancellationToken ct) =>
            Results.Ok(await service.GetPartUrlsAsync(fileAttachmentId, dto.PartNumbers ?? Array.Empty<int>(), ct)));

        uploads.MapGet("/{fileAttachmentId:long}/parts", async (long fileAttachmentId, FileService service, CancellationToken ct) =>
            Results.Ok(await service.ListUploadedPartsAsync(fileAttachmentId, ct)));

        uploads.MapPost("/{fileAttachmentId:long}/complete", async (long fileAttachmentId, CompleteDto dto, FileService service, CancellationToken ct) =>
        {
            var file = await service.CompleteUploadAsync(fileAttachmentId, dto.Parts ?? Array.Empty<UploadedPart>(), ct);
            return Results.Ok(new { file.FileAttachmentId });
        });

        uploads.MapDelete("/{fileAttachmentId:long}", async (long fileAttachmentId, FileService service, CancellationToken ct) =>
        {
            await service.AbortUploadAsync(fileAttachmentId, ct);
            return Results.NoContent();
        });
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
        url = urls.GetUrl(f.StorageKey), // direct, signed, expiring - for thumbnails/previews
    };
}
