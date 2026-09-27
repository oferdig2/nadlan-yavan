using Dapper;
using Nadlan.Core.Portfolios;
using Nadlan.Core.Security;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Persistence.MySql.Portfolios;

public sealed class MySqlPortfolioStore : IPortfolioStore
{
    private readonly MySqlDatabase _db;

    public MySqlPortfolioStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<Portfolio?> GetAsync(long portfolioId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Portfolio>(new CommandDefinition("""
            SELECT portfolio_id AS PortfolioId, name AS Name, portfolio_type_id AS PortfolioTypeId, description AS Description
            FROM portfolio WHERE portfolio_id = @portfolioId
            """, new { portfolioId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PortfolioSummary>> SearchAsync(string? text, int limit, AccessScope scope, CancellationToken ct = default)
    {
        var args = new DynamicParameters();
        args.Add("text", string.IsNullOrWhiteSpace(text) ? null : text);
        args.Add("contains", $"%{SqlLike.Escape(text?.Trim() ?? "")}%");
        args.Add("limit", limit);
        var visible = AccessSql.PortfolioVisible("p", scope, args);
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<PortfolioSummary>(new CommandDefinition($"""
            SELECT p.portfolio_id AS PortfolioId, p.name AS Name, t.name AS TypeName,
                   (SELECT COUNT(*) FROM portfolio_asset pa WHERE pa.portfolio_id = p.portfolio_id) AS AssetCount
            FROM portfolio p
            JOIN portfolio_type t ON t.portfolio_type_id = p.portfolio_type_id
            WHERE (@text IS NULL OR p.name LIKE @contains) AND {visible}
            ORDER BY p.name
            LIMIT @limit
            """, args, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<long> InsertAsync(Portfolio portfolio, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO portfolio (name, portfolio_type_id, description) VALUES (@Name, @PortfolioTypeId, @Description);
            SELECT LAST_INSERT_ID();
            """, portfolio, cancellationToken: ct));
    }

    public async Task<int> AddAssetsAsync(long portfolioId, IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var next = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM portfolio_asset WHERE portfolio_id = @portfolioId FOR UPDATE",
            new { portfolioId }, tx, cancellationToken: ct));
        var existing = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT asset_id FROM portfolio_asset WHERE portfolio_id = @portfolioId", new { portfolioId }, tx, cancellationToken: ct))).ToHashSet();

        // Keep the caller's order and give new members consecutive sort orders; insert in multi-row batches
        // ("Select all" can send up to 2000 Assets).
        var toAdd = assetIds.Where(existing.Add).ToList();
        var added = 0;
        foreach (var batch in toAdd.Chunk(500))
        {
            var args = new DynamicParameters(new { portfolioId });
            var values = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                values.Add($"(@portfolioId, @a{i}, @s{i})");
                args.Add($"a{i}", batch[i]);
                args.Add($"s{i}", next + added + i);
            }

            // IGNORE: an Asset deleted meanwhile is skipped instead of failing the whole selection.
            added += await conn.ExecuteAsync(new CommandDefinition(
                $"INSERT IGNORE INTO portfolio_asset (portfolio_id, asset_id, sort_order) VALUES {string.Join(", ", values)}",
                args, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return added;
    }

    public async Task UpdateAsync(Portfolio portfolio, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE portfolio
            SET name = @Name, portfolio_type_id = @PortfolioTypeId, description = @Description, updated_utc = UTC_TIMESTAMP(3)
            WHERE portfolio_id = @PortfolioId
            """, portfolio, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<long>> ListAssetIdsAsync(long portfolioId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var ids = await conn.QueryAsync<long>(new CommandDefinition("""
            SELECT asset_id FROM portfolio_asset WHERE portfolio_id = @portfolioId
            ORDER BY COALESCE(sort_order, 2147483647), added_utc, asset_id
            """, new { portfolioId }, cancellationToken: ct));
        return ids.ToList();
    }

    public async Task ReorderAsync(long portfolioId, IReadOnlyList<long> orderedAssetIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        for (var i = 0; i < orderedAssetIds.Count; i++)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE portfolio_asset SET sort_order = @sortOrder WHERE portfolio_id = @portfolioId AND asset_id = @assetId",
                new { portfolioId, assetId = orderedAssetIds[i], sortOrder = i }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<bool> RemoveAssetAsync(long portfolioId, long assetId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM portfolio_asset WHERE portfolio_id = @portfolioId AND asset_id = @assetId",
            new { portfolioId, assetId }, cancellationToken: ct)) > 0;
    }
}
