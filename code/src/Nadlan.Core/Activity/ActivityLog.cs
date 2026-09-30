namespace Nadlan.Core.Activity;

/// <summary>What happened, for the audit trail (spec §4 Activity: Phase 1 audited actions).</summary>
public static class ActivityActions
{
    public const string ParcelCreated = "ParcelCreated";
    public const string ParcelEdited = "ParcelEdited";
    public const string ParcelGeometryChanged = "ParcelGeometryChanged";
    public const string ParcelDeleted = "ParcelDeleted";
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
    public const string UserCreated = "UserCreated";
    public const string UserEdited = "UserEdited";
    public const string UserDeleted = "UserDeleted";
    public const string UserSignedOut = "UserSignedOut";
    public const string UserUnlocked = "UserUnlocked";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordReset = "PasswordReset";
    public const string PasswordSetByAdmin = "PasswordSetByAdmin";
    public const string PasswordRemoved = "PasswordRemoved";
    public const string ResetLinkCreated = "ResetLinkCreated";
    public const string AccessGranted = "AccessGranted";
    public const string AccessRevoked = "AccessRevoked";
    public const string RoleChanged = "RoleChanged";
}

public sealed record ActivityEntry(string EntityType, long EntityId, string ActionType, string Summary, object? Metadata = null)
{
    /// <summary>Who did it. Filled in by the host from the signed-in user; null for tools/imports.</summary>
    public long? UserId { get; init; }
}

public sealed record ActivityItem(long ActivityId, string EntityType, long EntityId, string ActionType, string Summary, string? MetadataJson, DateTime CreatedUtc)
{
    public long? UserId { get; init; }
    public string? UserName { get; init; }
}

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
