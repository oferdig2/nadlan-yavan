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
        await InsertAsync(conn, null, entry, ct);
    }

    /// <summary>The insert itself, also used inside another store's transaction (the row save writes its history with its data).</summary>
    internal static Task InsertAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction? tx, ActivityEntry entry, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition("""
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
            }, tx, cancellationToken: ct));

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

    public async Task<IReadOnlyList<ActivityItem>> ListByUserAsync(long userId, string? entityType, DateTime? fromUtc, DateTime? toUtc, int limit,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ActivityRow>(new CommandDefinition("""
            SELECT a.activity_id, a.entity_type, a.entity_id, a.action_type, a.summary,
                   CAST(a.metadata_json AS CHAR) AS metadata_json, a.created_utc, a.user_id, u.display_name AS user_name,
                   p.registry_id AS entity_label
            FROM activity a
            LEFT JOIN app_user u ON u.user_id = a.user_id
            LEFT JOIN parcel p ON a.entity_type = 'Parcel' AND p.parcel_id = a.entity_id
            WHERE a.user_id = @userId
              AND (@entityType IS NULL OR a.entity_type = @entityType)
              AND (@fromUtc IS NULL OR a.created_utc >= @fromUtc)
              AND (@toUtc IS NULL OR a.created_utc < @toUtc)
            ORDER BY a.created_utc DESC, a.activity_id DESC
            LIMIT @limit
            """, new { userId, entityType, fromUtc, toUtc, limit }, cancellationToken: ct)); // ix_activity_user (user_id, created_utc)
        return rows.Select(r => new ActivityItem(r.ActivityId, r.EntityType, r.EntityId, r.ActionType, r.Summary, r.MetadataJson, r.CreatedUtc)
        {
            UserId = r.UserId,
            UserName = r.UserName,
            EntityLabel = r.EntityLabel,
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
        public string? EntityLabel { get; init; }
    }
}
