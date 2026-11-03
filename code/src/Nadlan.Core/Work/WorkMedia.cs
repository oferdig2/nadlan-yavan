namespace Nadlan.Core.Work;

/// <summary>A picture or video of one work (work_file), uploaded by the contractor. Not a Parcel file until staff copy it.</summary>
public sealed record WorkFile
{
    public long WorkFileId { get; init; }
    public long WorkItemId { get; init; }
    public long WorkRequestId { get; init; }
    public long ParcelId { get; init; }
    public string? RegistryId { get; init; }
    public string WorkTypeName { get; init; } = "";
    public string StorageKey { get; init; } = "";
    public string OriginalName { get; init; } = "";
    public string MimeType { get; init; } = "";
    public long FileSize { get; init; }
    public string UploadStatus { get; init; } = Files.FileUploadStatus.Pending;
    public string? S3UploadId { get; init; }
    public bool HasThumbnail { get; init; }
    public string? TakenLocal { get; init; }
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public decimal? DurationS { get; init; }
    public double? UploadLatitude { get; init; }
    public double? UploadLongitude { get; init; }
    public decimal? UploadAccuracyM { get; init; }
    public DateTime UploadedUtc { get; init; }
    public long? CopiedToFileAttachmentId { get; init; }
}

/// <summary>Where the phone was when the upload started (the contractor's own position, if they agreed to share it).</summary>
public sealed record UploadPosition(double Latitude, double Longitude, decimal? AccuracyM);

/// <summary>One message of a Parcel's conversation. AuthorName: the staff user (staff see it; the contractor sees "GreekPlot").</summary>
public sealed record WorkMessage
{
    public long WorkMessageId { get; init; }
    public long ParcelId { get; init; }
    public string AuthorKind { get; init; } = "";
    public string? AuthorName { get; init; }
    public string Body { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
}

/// <summary>A customer-facing photo / video of a Parcel of the request (Marketing, a type a browser shows).</summary>
public sealed record WorkParcelMedia
{
    public long FileAttachmentId { get; init; }
    public long ParcelId { get; init; }
    public string StorageKey { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string MimeType { get; init; } = "";
    public bool HasThumbnail { get; init; }
    public string? TakenLocal { get; init; }
}
