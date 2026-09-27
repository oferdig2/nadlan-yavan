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
            INSERT INTO activity (entity_type, entity_id, action_type, summary, metadata_json)
            VALUES (@EntityType, @EntityId, @ActionType, @Summary, @MetadataJson)
            """, new
            {
                entry.EntityType,
                entry.EntityId,
                entry.ActionType,
                Summary = entry.Summary.Length <= 500 ? entry.Summary : entry.Summary[..497] + "...",
                MetadataJson = entry.Metadata is null ? null : JsonSerializer.Serialize(entry.Metadata, Json),
            }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ActivityItem>> ListAsync(string entityType, long entityId, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ActivityItem>(new CommandDefinition("""
            SELECT activity_id AS ActivityId, entity_type AS EntityType, entity_id AS EntityId, action_type AS ActionType,
                   summary AS Summary, CAST(metadata_json AS CHAR) AS MetadataJson, created_utc AS CreatedUtc
            FROM activity
            WHERE entity_type = @entityType AND entity_id = @entityId
            ORDER BY created_utc DESC, activity_id DESC
            LIMIT @limit
            """, new { entityType, entityId, limit }, cancellationToken: ct));
        return rows.ToList();
    }
}
