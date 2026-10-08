namespace Nadlan.Core.Files;

public sealed record UploadedPart(int PartNumber, string ETag);

/// <summary>Storage could not be reached or refused us (credentials, permissions, bucket). API returns 503 with the reason.</summary>
public sealed class StorageUnavailableException : Exception
{
    public StorageUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Object storage for file bytes (S3). The app never carries the bytes: it only authorises the browser to
/// upload parts directly (presigned URLs) and to read files (<see cref="IFileUrlProvider"/>).
/// </summary>
public interface IObjectStorage
{
    /// <summary>False when no bucket is configured; file features then report STORAGE_NOT_CONFIGURED.</summary>
    bool IsConfigured { get; }

    Task<string> StartMultipartUploadAsync(string key, string contentType, string contentDisposition, CancellationToken ct = default);
    string GetPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan lifetime);
    /// <summary>Parts S3 holds for the upload, or null when the multipart upload no longer exists (completed or aborted).</summary>
    Task<IReadOnlyList<UploadedPart>?> ListUploadedPartsAsync(string key, string uploadId, CancellationToken ct = default);
    Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken ct = default);
    Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default);

    /// <summary>Size of a stored object, or null if it does not exist.</summary>
    Task<long?> GetObjectSizeAsync(string key, CancellationToken ct = default);

    Task DeleteObjectAsync(string key, CancellationToken ct = default);

    /// <summary>Stores a small object in one request (preview images; files themselves go up multipart from the browser).</summary>
    Task PutObjectAsync(string key, byte[] content, string contentType, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>Objects whose key starts with <paramref name="prefix"/> (keys relative to the storage root, like every key here).</summary>
    Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>A small text object (at most <paramref name="maxBytes"/>), or null if it doesn't exist or is larger.</summary>
    Task<string?> ReadTextAsync(string key, int maxBytes, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>A short-lived link that downloads the object as a file named <paramref name="downloadName"/> (never shown inline).</summary>
    string GetAttachmentUrl(string key, string downloadName, TimeSpan lifetime) => throw new NotSupportedException();
}

public sealed record StoredObject(string Key, long Size, DateTimeOffset LastModifiedUtc);

/// <summary>
/// Turns a storage key into a URL the browser can load: an S3 presigned URL (dev), a CloudFront signed URL,
/// or a public CloudFront URL, depending on configuration. Callers never build URLs themselves.
/// </summary>
public interface IFileUrlProvider
{
    string GetUrl(string storageKey);

    /// <summary>
    /// The same, opened inline under <paramref name="downloadName"/> instead of the uploader's file name (for customers:
    /// "photo.jpg", not "ID_scan_Papadopoulos.jpg"). Delivery modes that can't override it ignore the name.
    /// </summary>
    string GetUrl(string storageKey, string downloadName) => GetUrl(storageKey);
}
