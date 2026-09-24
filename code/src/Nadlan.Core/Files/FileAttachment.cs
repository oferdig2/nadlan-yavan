namespace Nadlan.Core.Files;

/// <summary>What a file is attached to. One generic model for every entity (spec §4 FileAttachment).</summary>
public static class FileTargetTypes
{
    public const string Parcel = "Parcel";
    public const string Asset = "Asset";
    public const string Portfolio = "Portfolio";
    public const string Contact = "Contact";
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = new[] { Parcel, Asset, Portfolio, Contact, User };

    /// <summary>Returns the canonical spelling, or null if unknown.</summary>
    public static string? Normalize(string? value)
        => All.FirstOrDefault(t => string.Equals(t, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class FileUploadStatus
{
    public const string Pending = "Pending";
    public const string Ready = "Ready";
}

public sealed record FileAttachment
{
    public long FileAttachmentId { get; init; }
    public int FileTypeId { get; init; }
    public string AttachedToType { get; init; } = "";
    public long AttachedToId { get; init; }
    public string StorageKey { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string MimeType { get; init; } = "";
    public long FileSize { get; init; }
    public string? Caption { get; init; }
    public string? Notes { get; init; }
    public int? SortOrder { get; init; }
    public string UploadStatus { get; init; } = FileUploadStatus.Pending;
    public string? S3UploadId { get; init; }
    public DateTime UploadedUtc { get; init; }
}

/// <summary>A Ready file plus its type, for lists and cards.</summary>
public sealed record FileListItem
{
    public long FileAttachmentId { get; init; }
    public int FileTypeId { get; init; }
    public string FileTypeCode { get; init; } = "";
    public string FileTypeName { get; init; } = "";
    public string Category { get; init; } = "";
    public string AttachedToType { get; init; } = "";
    public long AttachedToId { get; init; }
    public string StorageKey { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string MimeType { get; init; } = "";
    public long FileSize { get; init; }
    public string? Caption { get; init; }
    public string? Notes { get; init; }
    public int? SortOrder { get; init; }
    public DateTime UploadedUtc { get; init; }
}

public sealed record FileType(int Id, string Code, string Name, string Category, bool IsActive, int SortOrder);

public interface IFileAttachmentStore
{
    Task<long> InsertPendingAsync(FileAttachment file, CancellationToken ct = default);
    Task<FileAttachment?> GetAsync(long fileAttachmentId, CancellationToken ct = default);
    Task<IReadOnlyList<FileListItem>> ListReadyAsync(string attachedToType, long attachedToId, CancellationToken ct = default);
    Task MarkReadyAsync(long fileAttachmentId, CancellationToken ct = default);
    Task UpdateMetadataAsync(long fileAttachmentId, int fileTypeId, string? caption, string? notes, int? sortOrder, CancellationToken ct = default);
    Task DeleteAsync(long fileAttachmentId, CancellationToken ct = default);
    Task<IReadOnlyList<FileType>> ListTypesAsync(CancellationToken ct = default);
}

/// <summary>Checks that the entity a file is being attached to exists.</summary>
public interface IFileTargetResolver
{
    Task<bool> ExistsAsync(string attachedToType, long attachedToId, CancellationToken ct = default);
}
