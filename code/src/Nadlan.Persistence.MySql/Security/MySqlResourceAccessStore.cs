using Dapper;
using Nadlan.Core.Security;

namespace Nadlan.Persistence.MySql.Security;

public sealed class MySqlResourceAccessStore : IResourceAccessStore
{
    // A readable label per granted object, e.g. "Asset #12 · 050981234567 · Agent A".
    private const string LabelSql = """
        CASE ra.resource_type
          WHEN 'Asset' THEN (SELECT CONCAT('Asset #', a.asset_id, COALESCE(CONCAT(' · ', MIN(p.registry_id)), ''), ' · ', c.display_name)
                             FROM asset a JOIN contact c ON c.contact_id = a.managing_contact_id
                             LEFT JOIN asset_parcel ap ON ap.asset_id = a.asset_id LEFT JOIN parcel p ON p.parcel_id = ap.parcel_id
                             WHERE a.asset_id = ra.resource_id GROUP BY a.asset_id, c.display_name)
          WHEN 'Parcel' THEN (SELECT CONCAT('Parcel ', COALESCE(p.registry_id, CONCAT('#', p.parcel_id))) FROM parcel p WHERE p.parcel_id = ra.resource_id)
          WHEN 'Portfolio' THEN (SELECT pf.name FROM portfolio pf WHERE pf.portfolio_id = ra.resource_id)
          WHEN 'Contact' THEN (SELECT c.display_name FROM contact c WHERE c.contact_id = ra.resource_id)
        END
        """;

    private static readonly string Select = $"""
        SELECT ra.resource_access_id, ra.user_id, ra.resource_type, ra.resource_id, ({LabelSql}) AS resource_label,
               p.code AS permission_code, p.name AS permission_name, ra.granted_by_user_id, g.display_name AS granted_by_name,
               ra.created_utc, ra.expires_utc
        FROM resource_access ra
        JOIN permission p ON p.permission_id = ra.permission_id
        LEFT JOIN app_user g ON g.user_id = ra.granted_by_user_id
        """;

    private readonly MySqlDatabase _db;

    public MySqlResourceAccessStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ResourceGrant>> ListForUserAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ResourceGrant>(new CommandDefinition(
            $"{Select} WHERE ra.user_id = @userId ORDER BY ra.resource_type, ra.resource_id, p.sort_order", new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<ResourceGrant?> GetAsync(long resourceAccessId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ResourceGrant>(new CommandDefinition(
            $"{Select} WHERE ra.resource_access_id = @resourceAccessId", new { resourceAccessId }, cancellationToken: ct));
    }

    public async Task<long> GrantAsync(long userId, string resourceType, long resourceId, int permissionId, long? grantedByUserId,
        DateTime? expiresUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO resource_access (user_id, resource_type, resource_id, permission_id, granted_by_user_id, expires_utc)
            VALUES (@userId, @resourceType, @resourceId, @permissionId, @grantedByUserId, @expiresUtc)
            ON DUPLICATE KEY UPDATE expires_utc = VALUES(expires_utc), granted_by_user_id = VALUES(granted_by_user_id)
            """, new { userId, resourceType, resourceId, permissionId, grantedByUserId, expiresUtc }, cancellationToken: ct));
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT resource_access_id FROM resource_access
            WHERE user_id = @userId AND resource_type = @resourceType AND resource_id = @resourceId AND permission_id = @permissionId
            """, new { userId, resourceType, resourceId, permissionId }, cancellationToken: ct));
    }

    public async Task<bool> RevokeAsync(long resourceAccessId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM resource_access WHERE resource_access_id = @resourceAccessId", new { resourceAccessId }, cancellationToken: ct)) > 0;
    }

    public async Task<int> RevokeGrantedByAsync(long grantedByUserId, string resourceType, long resourceId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM resource_access
            WHERE granted_by_user_id = @grantedByUserId AND resource_type = @resourceType AND resource_id = @resourceId
            """, new { grantedByUserId, resourceType, resourceId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ResourceRef>> SearchResourcesAsync(string resourceType, string? text, int limit, CancellationToken ct = default)
    {
        var t = text?.Trim() ?? "";
        var like = $"%{SqlLike.Escape(t)}%";
        long? id = long.TryParse(t.TrimStart('#'), out var n) ? n : null;
        var sql = resourceType switch
        {
            ResourceTypes.Asset => """
                SELECT a.asset_id AS Id,
                       CONCAT('Asset #', a.asset_id, COALESCE(CONCAT(' · ', MIN(p.registry_id)), '')) AS Label,
                       CONCAT(c.display_name, ' · ', s.name) AS Detail
                FROM asset a
                JOIN contact c ON c.contact_id = a.managing_contact_id
                JOIN asset_status s ON s.asset_status_id = a.asset_status_id
                LEFT JOIN asset_parcel ap ON ap.asset_id = a.asset_id
                LEFT JOIN parcel p ON p.parcel_id = ap.parcel_id
                WHERE @t = '' OR a.asset_id = @id OR p.registry_id LIKE @like OR c.display_name LIKE @like
                GROUP BY a.asset_id, c.display_name, s.name
                ORDER BY a.asset_id DESC LIMIT @limit
                """,
            ResourceTypes.Parcel => """
                SELECT p.parcel_id AS Id, CONCAT('Parcel ', COALESCE(p.registry_id, CONCAT('#', p.parcel_id))) AS Label,
                       CONCAT_WS(' · ', ga.name, CONCAT('OT ', p.ot), CONCAT('plot ', p.plot_number)) AS Detail
                FROM parcel p LEFT JOIN geographic_area ga ON ga.geographic_area_id = p.geographic_area_id
                WHERE @t = '' OR p.parcel_id = @id OR p.registry_id LIKE @like
                ORDER BY p.parcel_id DESC LIMIT @limit
                """,
            ResourceTypes.Portfolio => """
                SELECT pf.portfolio_id AS Id, pf.name AS Label, t.name AS Detail
                FROM portfolio pf JOIN portfolio_type t ON t.portfolio_type_id = pf.portfolio_type_id
                WHERE @t = '' OR pf.portfolio_id = @id OR pf.name LIKE @like
                ORDER BY pf.name LIMIT @limit
                """,
            ResourceTypes.Contact => """
                SELECT c.contact_id AS Id, c.display_name AS Label, COALESCE(c.email, c.cell_phone, c.phone) AS Detail
                FROM contact c
                WHERE @t = '' OR c.contact_id = @id OR c.display_name LIKE @like OR c.email LIKE @like
                ORDER BY c.display_name LIMIT @limit
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, "Unknown resource type."),
        };

        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ResourceRow>(new CommandDefinition(sql, new { t, id, like, limit }, cancellationToken: ct));
        return rows.Select(r => new ResourceRef(r.Id, r.Label, r.Detail)).ToList();
    }

    public async Task<bool> ResourceExistsAsync(string resourceType, long resourceId, CancellationToken ct = default)
    {
        var (table, column) = resourceType switch
        {
            ResourceTypes.Asset => ("asset", "asset_id"),
            ResourceTypes.Parcel => ("parcel", "parcel_id"),
            ResourceTypes.Portfolio => ("portfolio", "portfolio_id"),
            ResourceTypes.Contact => ("contact", "contact_id"),
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, "Unknown resource type."),
        };
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS (SELECT 1 FROM {table} WHERE {column} = @resourceId)", new { resourceId }, cancellationToken: ct));
    }

    private sealed class ResourceRow
    {
        public long Id { get; init; }
        public string Label { get; init; } = "";
        public string? Detail { get; init; }
    }
}

public sealed class MySqlPasswordTokenStore : IPasswordTokenStore
{
    private readonly MySqlDatabase _db;

    public MySqlPasswordTokenStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task CreateAsync(string tokenHash, long userId, string purpose, DateTime expiresUtc, long? createdByUserId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Only the newest link works; old unused ones (and expired leftovers) go.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM password_token WHERE user_id = @userId AND used_utc IS NULL", new { userId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO password_token (token_hash, user_id, purpose, created_by_user_id, expires_utc)
            VALUES (@tokenHash, @userId, @purpose, @createdByUserId, @expiresUtc)
            """, new { tokenHash, userId, purpose, createdByUserId, expiresUtc }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<PasswordToken?> GetAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TokenRow>(new CommandDefinition(
            "SELECT token_hash, user_id, purpose, expires_utc, used_utc FROM password_token WHERE token_hash = @tokenHash",
            new { tokenHash }, cancellationToken: ct));
        return row is null ? null : new PasswordToken(row.TokenHash, row.UserId, row.Purpose, row.ExpiresUtc, row.UsedUtc);
    }

    public async Task<bool> MarkUsedAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE password_token SET used_utc = UTC_TIMESTAMP(3) WHERE token_hash = @tokenHash AND used_utc IS NULL",
            new { tokenHash }, cancellationToken: ct)) > 0;
    }

    private sealed class TokenRow
    {
        public string TokenHash { get; init; } = "";
        public long UserId { get; init; }
        public string Purpose { get; init; } = "";
        public DateTime ExpiresUtc { get; init; }
        public DateTime? UsedUtc { get; init; }
    }
}

public sealed class MySqlApiTokenStore : IApiTokenStore
{
    private readonly MySqlDatabase _db;

    public MySqlApiTokenStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<long> CreateAsync(long userId, string name, string tokenHash, string tokenPrefix, DateTime? expiresUtc,
        long? createdByUserId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO api_token (user_id, name, token_hash, token_prefix, expires_utc, created_by_user_id)
            VALUES (@userId, @name, @tokenHash, @tokenPrefix, @expiresUtc, @createdByUserId);
            SELECT LAST_INSERT_ID();
            """, new { userId, name, tokenHash, tokenPrefix, expiresUtc, createdByUserId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ApiToken>> ListForUserAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<TokenRow>(new CommandDefinition("""
            SELECT api_token_id, user_id, name, token_prefix, created_utc, last_used_utc, expires_utc, revoked_utc
            FROM api_token WHERE user_id = @userId ORDER BY created_utc DESC
            """, new { userId }, cancellationToken: ct));
        return rows.Select(r => new ApiToken(r.ApiTokenId, r.UserId, r.Name, r.TokenPrefix, r.CreatedUtc, r.LastUsedUtc, r.ExpiresUtc, r.RevokedUtc)).ToList();
    }

    public async Task<bool> RevokeAsync(long userId, long apiTokenId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE api_token SET revoked_utc = UTC_TIMESTAMP(3)
            WHERE api_token_id = @apiTokenId AND user_id = @userId AND revoked_utc IS NULL
            """, new { userId, apiTokenId }, cancellationToken: ct)) > 0;
    }

    public async Task<int> RevokeByNameAsync(long userId, string name, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE api_token SET revoked_utc = UTC_TIMESTAMP(3) WHERE user_id = @userId AND name = @name AND revoked_utc IS NULL",
            new { userId, name }, cancellationToken: ct));
    }

    public async Task<long?> UseAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var userId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT user_id FROM api_token
            WHERE token_hash = @tokenHash AND revoked_utc IS NULL AND (expires_utc IS NULL OR expires_utc > UTC_TIMESTAMP(3))
            """, new { tokenHash }, cancellationToken: ct));
        if (userId is not null)
        {
            // At most one write a minute per token, not one per request.
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE api_token SET last_used_utc = UTC_TIMESTAMP(3)
                WHERE token_hash = @tokenHash AND (last_used_utc IS NULL OR last_used_utc < UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE)
                """, new { tokenHash }, cancellationToken: ct));
        }

        return userId;
    }

    private sealed class TokenRow
    {
        public long ApiTokenId { get; init; }
        public long UserId { get; init; }
        public string Name { get; init; } = "";
        public string TokenPrefix { get; init; } = "";
        public DateTime CreatedUtc { get; init; }
        public DateTime? LastUsedUtc { get; init; }
        public DateTime? ExpiresUtc { get; init; }
        public DateTime? RevokedUtc { get; init; }
    }
}
