using Nadlan.Core.Activity;
using Nadlan.Host.Auth;

namespace Nadlan.Host.Activity;

/// <summary>
/// Audit entries are written after the business change has committed. A failing audit insert must not turn
/// that committed change into a 500 (a retry would then hit "duplicate KAEK" etc.), so it is logged and dropped.
/// </summary>
internal sealed class BestEffortActivityLog : IActivityLog
{
    private readonly IActivityLog _inner;
    private readonly ILogger<BestEffortActivityLog> _logger;
    private readonly IHttpContextAccessor _http;

    public BestEffortActivityLog(IActivityLog inner, ILogger<BestEffortActivityLog> logger, IHttpContextAccessor http)
    {
        _inner = inner;
        _logger = logger;
        _http = http;
    }

    public async Task RecordAsync(ActivityEntry entry, CancellationToken ct = default)
    {
        try
        {
            // Who did it: the signed-in user of this request (null for background work such as the upload sweeper).
            await _inner.RecordAsync(entry.UserId is null ? entry with { UserId = _http.HttpContext?.GetUserAccess()?.UserId } : entry, ct);
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
