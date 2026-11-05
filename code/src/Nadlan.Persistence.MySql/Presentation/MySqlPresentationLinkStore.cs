using Dapper;
using Nadlan.Core.Presentation;

namespace Nadlan.Persistence.MySql.Presentation;

/// <summary>Public presentation links (migration 025).</summary>
public sealed class MySqlPresentationLinkStore : IPresentationLinkStore
{
    private const string Select = """
        SELECT l.presentation_link_id, l.portfolio_id, pf.name AS portfolio_name, l.asset_ids, l.title, l.link_token_hash, l.link_token_sealed,
               l.link_enabled, l.link_expires_utc, l.password_hash, l.password_version, l.failed_unlocks, l.locked_until_utc,
               l.open_count, l.last_opened_utc, l.created_by_user_id, u.display_name AS created_by_name, l.created_utc
        FROM presentation_link l
        JOIN app_user u ON u.user_id = l.created_by_user_id
        LEFT JOIN portfolio pf ON pf.portfolio_id = l.portfolio_id
        """;

    private readonly MySqlDatabase _db;

    public MySqlPresentationLinkStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<long> CreateAsync(NewPresentationLink link, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO presentation_link (portfolio_id, asset_ids, title, link_token_hash, link_token_sealed, link_expires_utc, created_by_user_id)
            VALUES (@PortfolioId, @AssetIds, @Title, @TokenHash, @TokenSealed, @ExpiresUtc, @CreatedByUserId);
            SELECT LAST_INSERT_ID();
            """, link, cancellationToken: ct));
    }

    public async Task<PresentationLink?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PresentationLink>(new CommandDefinition(Select + " WHERE l.presentation_link_id = @id",
            new { id }, cancellationToken: ct));
    }

    public async Task<PresentationLink?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PresentationLink>(new CommandDefinition(Select + " WHERE l.link_token_hash = @tokenHash",
            new { tokenHash }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PresentationLink>> ListAsync(long? portfolioId, string? assetIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<PresentationLink>(new CommandDefinition(Select + """
             WHERE (@portfolioId IS NOT NULL AND l.portfolio_id = @portfolioId) OR (@portfolioId IS NULL AND l.asset_ids = @assetIds)
             ORDER BY l.created_utc DESC, l.presentation_link_id DESC
            """, new { portfolioId, assetIds }, cancellationToken: ct))).ToList();
    }

    public Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default) => UpdateAsync("link_enabled = @enabled", new { id, enabled }, ct);

    public Task SetExpiryAsync(long id, DateTime expiresUtc, CancellationToken ct = default)
        => UpdateAsync("link_expires_utc = @expiresUtc", new { id, expiresUtc }, ct);

    public Task SetTokenAsync(long id, string tokenHash, string tokenSealed, CancellationToken ct = default)
        => UpdateAsync("link_token_hash = @tokenHash, link_token_sealed = @tokenSealed", new { id, tokenHash, tokenSealed }, ct);

    public Task SetPasswordAsync(long id, string? passwordHash, CancellationToken ct = default)
        => UpdateAsync("password_hash = @passwordHash, password_version = password_version + 1, failed_unlocks = 0, locked_until_utc = NULL",
            new { id, passwordHash }, ct);

    public Task SetTitleAsync(long id, string? title, CancellationToken ct = default) => UpdateAsync("title = @title", new { id, title }, ct);

    public async Task<int> CountWrongUnlockAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            UPDATE presentation_link SET failed_unlocks = failed_unlocks + 1 WHERE presentation_link_id = @id;
            SELECT failed_unlocks FROM presentation_link WHERE presentation_link_id = @id;
            """, new { id }, cancellationToken: ct));
    }

    public Task LockAsync(long id, DateTime untilUtc, CancellationToken ct = default)
        => UpdateAsync("locked_until_utc = @untilUtc, failed_unlocks = 0", new { id, untilUtc }, ct);

    public Task ResetUnlocksAsync(long id, CancellationToken ct = default) => UpdateAsync("failed_unlocks = 0, locked_until_utc = NULL", new { id }, ct);

    public async Task RecordOpenAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE presentation_link SET open_count = open_count + 1, last_opened_utc = UTC_TIMESTAMP(3) WHERE presentation_link_id = @id",
            new { id }, cancellationToken: ct));
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM presentation_link WHERE presentation_link_id = @id", new { id }, cancellationToken: ct));
    }

    private async Task UpdateAsync(string set, object args, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE presentation_link SET " + set + ", updated_utc = UTC_TIMESTAMP(3) WHERE presentation_link_id = @id", args, cancellationToken: ct));
    }
}
