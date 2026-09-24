using System.Text;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Files;

public sealed record FileStorageSettings
{
    public long PartSizeBytes { get; init; } = 8L * 1024 * 1024;
    public long MaxFileSizeBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public TimeSpan PartUrlLifetime { get; init; } = TimeSpan.FromHours(1);
}

public sealed record StartUploadRequest(string AttachedToType, long AttachedToId, int FileTypeId, string FileName, string? MimeType, long FileSize);

/// <summary>What the browser needs to upload the bytes: parts of PartSizeBytes (the last one may be smaller).</summary>
public sealed record UploadSession(long FileAttachmentId, long PartSizeBytes, int PartCount);

public sealed record PartUrl(int PartNumber, string Url);

/// <summary>
/// Direct-to-S3 multipart upload flow (Appendix 1 §6): start → part URLs → browser PUTs parts → complete.
/// Every file is multipart, so one code path handles a 50 KB PDF and a 10 GB drone video, with per-part retry.
/// </summary>
public sealed class FileService
{
    // S3 limits: at most 10,000 parts; every part except the last must be >= 5 MiB.
    private const int MaxParts = 10_000;
    private const long MinPartBytes = 5L * 1024 * 1024;
    private const int MaxPartUrlsPerCall = 100;

    private readonly IFileAttachmentStore _files;
    private readonly IFileTargetResolver _targets;
    private readonly IObjectStorage _storage;
    private readonly FileStorageSettings _settings;

    public FileService(IFileAttachmentStore files, IFileTargetResolver targets, IObjectStorage storage, FileStorageSettings settings)
    {
        _files = files;
        _targets = targets;
        _storage = storage;
        _settings = settings;
    }

    public async Task<UploadSession> StartUploadAsync(StartUploadRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        var targetType = FileTargetTypes.Normalize(request.AttachedToType)
            ?? throw new DomainValidationException("FILE_TARGET_INVALID", "Files can be attached to a Parcel, Asset, Portfolio, Contact or User.");
        if (!await _targets.ExistsAsync(targetType, request.AttachedToId, ct))
        {
            throw new DomainValidationException("FILE_TARGET_NOT_FOUND", $"{targetType} {request.AttachedToId} does not exist.");
        }

        await EnsureFileTypeAsync(request.FileTypeId, allowInactive: false, ct);
        if (request.FileSize <= 0)
        {
            throw new DomainValidationException("FILE_EMPTY", "The file is empty.");
        }

        if (request.FileSize > _settings.MaxFileSizeBytes)
        {
            throw new DomainValidationException("FILE_TOO_LARGE", $"Files can be at most {_settings.MaxFileSizeBytes / (1024 * 1024 * 1024)} GB.");
        }

        var fileName = CleanFileName(request.FileName);
        var mime = string.IsNullOrWhiteSpace(request.MimeType) ? "application/octet-stream" : request.MimeType.Trim();
        var partSize = PartSizeFor(request.FileSize);
        var key = BuildKey(targetType, request.AttachedToId, fileName);

        var uploadId = await _storage.StartMultipartUploadAsync(key, mime, ContentDisposition(fileName, mime), ct);
        var id = await _files.InsertPendingAsync(new FileAttachment
        {
            FileTypeId = request.FileTypeId,
            AttachedToType = targetType,
            AttachedToId = request.AttachedToId,
            StorageKey = key,
            OriginalFileName = fileName,
            MimeType = mime,
            FileSize = request.FileSize,
            UploadStatus = FileUploadStatus.Pending,
            S3UploadId = uploadId,
        }, ct);

        return new UploadSession(id, partSize, (int)((request.FileSize + partSize - 1) / partSize));
    }

    public async Task<IReadOnlyList<PartUrl>> GetPartUrlsAsync(long fileAttachmentId, IReadOnlyList<int> partNumbers, CancellationToken ct = default)
    {
        var file = await GetPendingAsync(fileAttachmentId, ct);
        if (partNumbers.Count is 0 or > MaxPartUrlsPerCall || partNumbers.Any(n => n is < 1 or > MaxParts))
        {
            throw new DomainValidationException("FILE_PARTS_INVALID", $"Ask for 1-{MaxPartUrlsPerCall} part numbers between 1 and {MaxParts}.");
        }

        return partNumbers.Distinct()
            .Select(n => new PartUrl(n, _storage.GetPartUploadUrl(file.StorageKey, file.S3UploadId!, n, _settings.PartUrlLifetime)))
            .ToList();
    }

    /// <summary>Parts S3 already holds - lets the browser resume an interrupted upload instead of starting over.</summary>
    public async Task<IReadOnlyList<UploadedPart>> ListUploadedPartsAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        var file = await GetPendingAsync(fileAttachmentId, ct);
        var parts = await _storage.ListUploadedPartsAsync(file.StorageKey, file.S3UploadId!, ct);
        if (parts is not null)
        {
            return parts;
        }

        // The multipart upload is gone. Either S3 completed it and our side failed afterwards (size check / DB),
        // or it was aborted (e.g. by the 1-day lifecycle rule). Decide from the object itself, so a finished
        // multi-GB file is never uploaded twice or left orphaned.
        if (await _storage.GetObjectSizeAsync(file.StorageKey, ct) == file.FileSize && await _files.MarkReadyAsync(fileAttachmentId, ct))
        {
            throw new DomainValidationException("FILE_NOT_UPLOADING", "This file is already fully uploaded.");
        }

        await _storage.DeleteObjectAsync(file.StorageKey, ct); // a partial/unknown object must not linger
        await _files.DeleteAsync(fileAttachmentId, ct);
        throw new EntityNotFoundException("File", fileAttachmentId); // the browser starts a fresh upload
    }

    public async Task<FileAttachment> CompleteUploadAsync(long fileAttachmentId, IReadOnlyList<UploadedPart> parts, CancellationToken ct = default)
    {
        var file = await GetPendingAsync(fileAttachmentId, ct);
        if (parts.Count == 0)
        {
            throw new DomainValidationException("FILE_PARTS_MISSING", "No uploaded parts were reported.");
        }

        await _storage.CompleteMultipartUploadAsync(file.StorageKey, file.S3UploadId!, parts.OrderBy(p => p.PartNumber).ToList(), ct);

        // Trust S3, not the browser: the stored object must have exactly the announced size.
        var size = await _storage.GetObjectSizeAsync(file.StorageKey, ct);
        if (size != file.FileSize)
        {
            await _storage.DeleteObjectAsync(file.StorageKey, ct);
            await _files.DeleteAsync(fileAttachmentId, ct);
            throw new DomainValidationException("FILE_SIZE_MISMATCH", $"Upload incomplete: stored {size?.ToString() ?? "nothing"} of {file.FileSize} bytes. Please upload again.");
        }

        if (!await _files.MarkReadyAsync(fileAttachmentId, ct))
        {
            // The upload was cancelled while S3 was completing it: the row is gone, so remove the object too
            // rather than leave a file in the bucket that nothing points to.
            await _storage.DeleteObjectAsync(file.StorageKey, ct);
            throw new DomainValidationException("FILE_UPLOAD_CANCELLED", "The upload was cancelled.");
        }

        return file with { UploadStatus = FileUploadStatus.Ready, S3UploadId = null };
    }

    /// <summary>Cancel: abort the multipart upload in S3 and forget the row.</summary>
    public async Task AbortUploadAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        var file = await GetPendingAsync(fileAttachmentId, ct);
        await _storage.AbortMultipartUploadAsync(file.StorageKey, file.S3UploadId!, ct);
        await _files.DeleteAsync(fileAttachmentId, ct);
    }

    public async Task UpdateMetadataAsync(long fileAttachmentId, int fileTypeId, string? caption, string? notes, int? sortOrder, CancellationToken ct = default)
    {
        var file = await _files.GetAsync(fileAttachmentId, ct) ?? throw new EntityNotFoundException("File", fileAttachmentId);
        await EnsureFileTypeAsync(fileTypeId, allowInactive: fileTypeId == file.FileTypeId, ct);
        await _files.UpdateMetadataAsync(fileAttachmentId, fileTypeId, TextNormalize.NullIfBlank(caption), TextNormalize.NullIfBlank(notes), sortOrder, ct);
    }

    public async Task DeleteAsync(long fileAttachmentId, CancellationToken ct = default)
    {
        var file = await _files.GetAsync(fileAttachmentId, ct) ?? throw new EntityNotFoundException("File", fileAttachmentId);
        EnsureConfigured();
        if (file.UploadStatus == FileUploadStatus.Pending && file.S3UploadId is not null)
        {
            await _storage.AbortMultipartUploadAsync(file.StorageKey, file.S3UploadId, ct);
        }
        else
        {
            await _storage.DeleteObjectAsync(file.StorageKey, ct);
        }

        await _files.DeleteAsync(fileAttachmentId, ct);
    }

    /// <summary>
    /// Aborts uploads left Pending longer than <paramref name="olderThan"/> (tab closed mid-upload) and removes their rows.
    /// The bucket lifecycle rule expires the parts after a day anyway; this keeps the table in step. Returns the count.
    /// </summary>
    public async Task<int> SweepAbandonedUploadsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        if (!_storage.IsConfigured)
        {
            return 0;
        }

        var swept = 0;
        foreach (var file in await _files.ListStalePendingAsync(DateTime.UtcNow - olderThan, limit: 500, ct))
        {
            if (file.S3UploadId is not null)
            {
                await _storage.AbortMultipartUploadAsync(file.StorageKey, file.S3UploadId, ct); // NoSuchUpload is fine
            }

            await _files.DeleteAsync(file.FileAttachmentId, ct);
            swept++;
        }

        return swept;
    }

    /// <summary>Smallest part size (whole MiB, >= configured size) that keeps the file within S3's 10,000 parts.</summary>
    internal long PartSizeFor(long fileSize)
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

    private async Task<FileAttachment> GetPendingAsync(long fileAttachmentId, CancellationToken ct)
    {
        EnsureConfigured();
        var file = await _files.GetAsync(fileAttachmentId, ct) ?? throw new EntityNotFoundException("File", fileAttachmentId);
        if (file.UploadStatus != FileUploadStatus.Pending || file.S3UploadId is null)
        {
            throw new DomainValidationException("FILE_NOT_UPLOADING", "This file is not being uploaded.");
        }

        return file;
    }

    private async Task EnsureFileTypeAsync(int fileTypeId, bool allowInactive, CancellationToken ct)
    {
        var types = await _files.ListTypesAsync(ct);
        if (!types.Any(t => t.Id == fileTypeId && (t.IsActive || allowInactive)))
        {
            throw new DomainValidationException("FILE_TYPE_REQUIRED", "Select a document type.");
        }
    }

    private void EnsureConfigured()
    {
        if (!_storage.IsConfigured)
        {
            throw new DomainValidationException("STORAGE_NOT_CONFIGURED", "File storage is not configured (Nadlan:Storage:Bucket in app_config).");
        }
    }

    // {target}/{id}/{guid}/{name}, relative to the storage root folder (added by IObjectStorage): unique, traceable,
    // grouped per entity, and unaffected when the files move to another bucket/folder.
    private string BuildKey(string targetType, long targetId, string fileName)
        => $"{targetType.ToLowerInvariant()}/{targetId}/{Guid.NewGuid():N}/{SafeKeyName(fileName)}";

    internal static string CleanFileName(string? name)
    {
        var baseName = Path.GetFileName((name ?? "").Replace('\\', '/')).Trim();
        if (baseName.Length == 0)
        {
            throw new DomainValidationException("FILE_NAME_REQUIRED", "The file has no name.");
        }

        return baseName.Length <= 255 ? baseName : baseName[..200] + Path.GetExtension(baseName);
    }

    /// <summary>ASCII-only key segment (CloudFront/S3 URL-safe); the original name is kept in the DB and headers.</summary>
    internal static string SafeKeyName(string fileName)
    {
        var sb = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        }

        var safe = sb.ToString().Trim('.', '_');
        if (safe.Length == 0)
        {
            safe = "file";
        }

        return safe.Length <= 120 ? safe : safe[..100] + Path.GetExtension(safe);
    }

    // Types a browser would execute rather than display (script in HTML/SVG/XML). Checked by type AND extension,
    // because the browser-declared type is not trusted.
    private static readonly string[] ActiveMimeTypes = { "text/html", "image/svg+xml", "application/xhtml+xml", "text/xml", "application/xml", "text/javascript", "application/javascript" };
    private static readonly string[] ActiveExtensions = { ".html", ".htm", ".xhtml", ".svg", ".svgz", ".xml", ".js", ".mjs" };

    /// <summary>
    /// inline so images/PDFs open in the browser; "active" content (HTML, SVG, XML, JS) is always a download so it can
    /// never run as a page. RFC 5987 filename* keeps Greek names intact on download.
    /// </summary>
    internal static string ContentDisposition(string fileName, string mimeType)
    {
        var mime = mimeType.Split(';')[0].Trim();
        var active = ActiveMimeTypes.Any(m => mime.StartsWith(m, StringComparison.OrdinalIgnoreCase))
                     || mime.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) // atom, rss, xhtml, svg ... all render as XML
                     || ActiveExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
        var ascii = SafeKeyName(fileName);
        var encoded = Uri.EscapeDataString(fileName);
        return $"{(active ? "attachment" : "inline")}; filename=\"{ascii}\"; filename*=UTF-8''{encoded}";
    }
}
