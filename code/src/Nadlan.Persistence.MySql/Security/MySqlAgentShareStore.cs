using Dapper;
using Nadlan.Core.Assets;

namespace Nadlan.Persistence.MySql.Security;

/// <summary>SQL of "an agent" for "Let agent know" (N2), shared with the Asset table's rows.</summary>
internal static class AgentShareSql
{
    public const string Source = "AGENT_SHARE";

    /// <summary>
    /// User <paramref name="u"/> is an agent: active, and their role neither sees nor edits every Asset (Admins and Data
    /// persons see the Asset anyway - nothing to share).
    /// </summary>
    public static string IsAgent(string u) => $"""
        {u}.is_active = 1
        AND NOT EXISTS (SELECT 1 FROM security_role ar WHERE ar.security_role_id = {u}.security_role_id AND ar.is_system = 1)
        AND NOT EXISTS (SELECT 1 FROM role_permission arp JOIN permission ap ON ap.permission_id = arp.permission_id
                        WHERE arp.security_role_id = {u}.security_role_id AND ap.code IN ('VIEW_ALL_ASSETS', 'EDIT_ALL_ASSETS'))
        """;
}

public sealed class MySqlAgentShareStore : IAgentShareStore
{
    private readonly MySqlDatabase _db;

    public MySqlAgentShareStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<(long UserId, string DisplayName)?> FindAgentAsync(long contactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<AgentRow>(new CommandDefinition(
            $"SELECT u.user_id, u.display_name FROM app_user u WHERE u.contact_id = @contactId AND {AgentShareSql.IsAgent("u")} LIMIT 1",
            new { contactId }, cancellationToken: ct));
        return row is null ? null : (row.UserId, row.DisplayName);
    }

    private sealed class AgentRow
    {
        public long UserId { get; init; }
        public string DisplayName { get; init; } = "";
    }

    public async Task<long?> GetSharedUserAsync(long assetId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT user_id FROM resource_access WHERE resource_type = 'Asset' AND resource_id = @assetId AND source = @source ORDER BY resource_access_id LIMIT 1",
            new { assetId, source = AgentShareSql.Source }, cancellationToken: ct));
    }

    public async Task SetAsync(long assetId, long? userId, long grantedByUserId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM resource_access
            WHERE resource_type = 'Asset' AND resource_id = @assetId AND source = @source AND (@userId IS NULL OR user_id <> @userId)
            """, new { assetId, userId, source = AgentShareSql.Source }, tx, cancellationToken: ct));
        if (userId is not null)
        {
            // View and edit (EDIT_ASSET includes view). A grant the agent already holds by hand stays as it is (and by hand).
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO resource_access (user_id, resource_type, resource_id, permission_id, granted_by_user_id, expires_utc, source)
                SELECT @userId, 'Asset', @assetId, permission_id, @grantedByUserId, NULL, @source FROM permission WHERE code = 'EDIT_ASSET'
                ON DUPLICATE KEY UPDATE resource_access_id = resource_access_id
                """, new { assetId, userId, grantedByUserId, source = AgentShareSql.Source }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }
}
