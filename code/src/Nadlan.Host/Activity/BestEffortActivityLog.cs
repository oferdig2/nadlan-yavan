using Nadlan.Core.Activity;

namespace Nadlan.Host.Activity;

/// <summary>
/// Audit entries are written after the business change has committed. A failing audit insert must not turn
/// that committed change into a 500 (a retry would then hit "duplicate KAEK" etc.), so it is logged and dropped.
/// </summary>
internal sealed class BestEffortActivityLog : IActivityLog
{
    private readonly IActivityLog _inner;
    private readonly ILogger<BestEffortActivityLog> _logger;

    public BestEffortActivityLog(IActivityLog inner, ILogger<BestEffortActivityLog> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async Task RecordAsync(ActivityEntry entry, CancellationToken ct = default)
    {
        try
        {
            await _inner.RecordAsync(entry, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Activity not recorded: {EntityType} {EntityId} {ActionType} - {Summary}",
                entry.EntityType, entry.EntityId, entry.ActionType, entry.Summary);
        }
    }

    public Task<IReadOnlyList<ActivityItem>> ListAsync(string entityType, long entityId, int limit, CancellationToken ct = default)
        => _inner.ListAsync(entityType, entityId, limit, ct);
}
