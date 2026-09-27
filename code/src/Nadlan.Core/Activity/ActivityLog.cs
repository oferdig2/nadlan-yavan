namespace Nadlan.Core.Activity;

/// <summary>What happened, for the audit trail (spec §4 Activity: Phase 1 audited actions).</summary>
public static class ActivityActions
{
    public const string ParcelCreated = "ParcelCreated";
    public const string ParcelEdited = "ParcelEdited";
    public const string ParcelGeometryChanged = "ParcelGeometryChanged";
    public const string LegalOwnerAdded = "LegalOwnerAdded";
    public const string LegalOwnerRemoved = "LegalOwnerRemoved";
    public const string AssetCreated = "AssetCreated";
    public const string AssetEdited = "AssetEdited";
    public const string AssetPriceChanged = "AssetPriceChanged";
    public const string AssetStatusChanged = "AssetStatusChanged";
    public const string AssetManagingContactChanged = "AssetManagingContactChanged";
    public const string AssetContactAdded = "AssetContactAdded";
    public const string AssetContactRemoved = "AssetContactRemoved";
    public const string PortfolioCreated = "PortfolioCreated";
    public const string PortfolioEdited = "PortfolioEdited";
    public const string PortfolioAssetAdded = "PortfolioAssetAdded";
    public const string PortfolioAssetRemoved = "PortfolioAssetRemoved";
    public const string ContactCreated = "ContactCreated";
    public const string ContactEdited = "ContactEdited";
    public const string FileUploaded = "FileUploaded";
    public const string FileDeleted = "FileDeleted";
}

public sealed record ActivityEntry(string EntityType, long EntityId, string ActionType, string Summary, object? Metadata = null);

public sealed record ActivityItem(long ActivityId, string EntityType, long EntityId, string ActionType, string Summary, string? MetadataJson, DateTime CreatedUtc);

public interface IActivityLog
{
    Task RecordAsync(ActivityEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<ActivityItem>> ListAsync(string entityType, long entityId, int limit, CancellationToken ct = default);
}

/// <summary>For tests and tools that don't audit.</summary>
public sealed class NullActivityLog : IActivityLog
{
    public static readonly NullActivityLog Instance = new();

    public Task RecordAsync(ActivityEntry entry, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ActivityItem>> ListAsync(string entityType, long entityId, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ActivityItem>>(Array.Empty<ActivityItem>());
}
