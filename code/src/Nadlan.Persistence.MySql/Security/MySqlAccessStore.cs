using Dapper;
using Nadlan.Core.Security;

namespace Nadlan.Persistence.MySql.Security;

public sealed class MySqlAccessStore : IAccessStore
{
    private readonly MySqlDatabase _db;

    public MySqlAccessStore(MySqlDatabase db)
    {
        _db = db;
    }

    public Task<bool> IsAssetVisibleAsync(AccessScope scope, long assetId, CancellationToken ct = default)
        => ExistsAsync("asset a", "a.asset_id", assetId, (args) => AccessSql.AssetVisible("a", scope, args), ct);

    public Task<bool> IsParcelVisibleAsync(AccessScope scope, long parcelId, CancellationToken ct = default)
        => ExistsAsync("parcel p", "p.parcel_id", parcelId, (args) => AccessSql.ParcelVisible("p", scope, args), ct);

    public Task<bool> IsPortfolioVisibleAsync(AccessScope scope, long portfolioId, CancellationToken ct = default)
        => ExistsAsync("portfolio pf", "pf.portfolio_id", portfolioId, (args) => AccessSql.PortfolioVisible("pf", scope, args), ct);

    public Task<bool> IsContactVisibleAsync(AccessScope scope, long contactId, CancellationToken ct = default)
        => ExistsAsync("contact c", "c.contact_id", contactId, (args) => AccessSql.ContactVisible("c", scope, args), ct);

    private async Task<bool> ExistsAsync(string table, string idColumn, long id, Func<DynamicParameters, string> predicate, CancellationToken ct)
    {
        var args = new DynamicParameters();
        args.Add("id", id);
        var sql = $"SELECT EXISTS (SELECT 1 FROM {table} WHERE {idColumn} = @id AND {predicate(args)})";
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, args, cancellationToken: ct));
    }

    public async Task<IReadOnlySet<string>> GetGrantCodesAsync(long userId, string resourceType, long resourceId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var codes = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT p.code
            FROM resource_access ra JOIN permission p ON p.permission_id = ra.permission_id
            WHERE ra.user_id = @userId AND ra.resource_type = @resourceType AND ra.resource_id = @resourceId
              AND (ra.expires_utc IS NULL OR ra.expires_utc > UTC_TIMESTAMP(3))
            """, new { userId, resourceType, resourceId }, cancellationToken: ct));
        return codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> GetPortfolioGrantCodesForAssetAsync(long userId, long assetId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var codes = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT p.code
            FROM portfolio_asset pa
            JOIN resource_access ra ON ra.resource_type = 'Portfolio' AND ra.resource_id = pa.portfolio_id
            JOIN permission p ON p.permission_id = ra.permission_id
            WHERE pa.asset_id = @assetId AND ra.user_id = @userId
              AND (ra.expires_utc IS NULL OR ra.expires_utc > UTC_TIMESTAMP(3))
            """, new { userId, assetId }, cancellationToken: ct));
        return codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
