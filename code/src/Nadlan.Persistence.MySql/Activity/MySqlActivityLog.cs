using System.Text.Json;
using Dapper;
using Nadlan.Core.Activity;

namespace Nadlan.Persistence.MySql.Activity;

public sealed class MySqlActivityLog : IActivityLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MySqlDatabase _db;

    public MySqlActivityLog(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task RecordAsync(ActivityEntry entry, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO activity (entity_type, entity_id, action_type, user_id, summary, metadata_json)
            VALUES (@EntityType, @EntityId, @ActionType, @UserId, @Summary, @MetadataJson)
            """, new
            {
                entry.EntityType,
                entry.EntityId,
                entry.ActionType,
                entry.UserId,
                Summary = entry.Summary.Length <= 500 ? entry.Summary : entry.Summary[..497] + "...",
                MetadataJson = entry.Metadata is null ? null : JsonSerializer.Serialize(entry.Metadata, Json),
            }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ActivityItem>> ListAsync(string entityType, long entityId, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ActivityRow>(new CommandDefinition("""
            SELECT a.activity_id, a.entity_type, a.entity_id, a.action_type, a.summary,
                   CAST(a.metadata_json AS CHAR) AS metadata_json, a.created_utc, a.user_id, u.display_name AS user_name
            FROM activity a
            LEFT JOIN app_user u ON u.user_id = a.user_id
            WHERE a.entity_type = @entityType AND a.entity_id = @entityId
            ORDER BY a.created_utc DESC, a.activity_id DESC
            LIMIT @limit
            """, new { entityType, entityId, limit }, cancellationToken: ct));
        return rows.Select(r => new ActivityItem(r.ActivityId, r.EntityType, r.EntityId, r.ActionType, r.Summary, r.MetadataJson, r.CreatedUtc)
        {
            UserId = r.UserId,
            UserName = r.UserName,
        }).ToList();
    }

    private sealed class ActivityRow
    {
        public long ActivityId { get; init; }
        public string EntityType { get; init; } = "";
        public long EntityId { get; init; }
        public string ActionType { get; init; } = "";
        public string Summary { get; init; } = "";
        public string? MetadataJson { get; init; }
        public DateTime CreatedUtc { get; init; }
        public long? UserId { get; init; }
        public string? UserName { get; init; }
    }
}
