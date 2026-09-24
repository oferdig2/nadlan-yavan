namespace Nadlan.Core.Import;

public static class ImportBatchStatus
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class ImportRecordStatus
{
    /// <summary>A new target entity was created from this row.</summary>
    public const string Created = "Created";

    /// <summary>The row resolved to an entity that already existed (duplicate row or re-run).</summary>
    public const string Linked = "Linked";

    /// <summary>The row was imported but something needs a human look (see ErrorMessage).</summary>
    public const string Warning = "Warning";

    /// <summary>The row could not be imported and waits in the review list.</summary>
    public const string NeedsReview = "NeedsReview";

    public const string Skipped = "Skipped";
}

public sealed record ImportRecord(
    long ImportBatchId,
    string LegacyTable,
    string LegacyRecordId,
    string TargetEntityType,
    long? TargetEntityId,
    string Status,
    string? ErrorMessage,
    string? RawDataJson);

public interface IImportStore
{
    Task<long> StartBatchAsync(string source, string sourceFile, long? importedByUserId, CancellationToken ct = default);
    Task AddRecordAsync(ImportRecord record, CancellationToken ct = default);
    Task CompleteBatchAsync(long importBatchId, string status, string summary, CancellationToken ct = default);
}
