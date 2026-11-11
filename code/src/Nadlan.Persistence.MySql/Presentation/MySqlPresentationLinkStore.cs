using Dapper;
using Nadlan.Core.Presentation;

namespace Nadlan.Persistence.MySql.Presentation;

/// <summary>Public presentation links (migration 025).</summary>
public sealed class MySqlPresentationLinkStore : IPresentationLinkStore
{
    private const string Select = """
        SELECT l.presentation_link_id, l.portfolio_id, pf.name AS portfolio_name, l.asset_ids, l.title, l.link_token_hash, l.link_token_sealed,
               l.link_enabled, l.link_expires_utc, l.password_hash, l.password_version, l.failed_unlocks, l.locked_until_utc,
               l.open_count, l.last_opened_utc, l.created_by_user_id, u.display_name AS created_by_name, l.created_utc,
               l.deleted_utc, l.contact_id, ct.display_name AS contact_name, ct.is_active AS contact_is_active
        FROM presentation_link l
        JOIN app_user u ON u.user_id = l.created_by_user_id
        LEFT JOIN portfolio pf ON pf.portfolio_id = l.portfolio_id
        LEFT JOIN contact ct ON ct.contact_id = l.contact_id
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
        return await conn.QuerySingleOrDefaultAsync<PresentationLink>(new CommandDefinition(Select + " WHERE l.presentation_link_id = @id AND l.deleted_utc IS NULL",
            new { id }, cancellationToken: ct));
    }

    public async Task<PresentationLink?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PresentationLink>(new CommandDefinition(Select + " WHERE l.link_token_hash = @tokenHash AND l.deleted_utc IS NULL",
            new { tokenHash }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PresentationLink>> ListAsync(long? portfolioId, string? assetIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<PresentationLink>(new CommandDefinition(Select + """
             WHERE l.deleted_utc IS NULL
               AND ((@portfolioId IS NOT NULL AND l.portfolio_id = @portfolioId) OR (@portfolioId IS NULL AND l.asset_ids = @assetIds))
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

    // Soft delete (migration 027): nothing removes a link row any more, so its visits keep their link.
    public Task DeleteAsync(long id, long byUserId, CancellationToken ct = default)
        => UpdateAsync("deleted_utc = COALESCE(deleted_utc, UTC_TIMESTAMP(3)), deleted_by_user_id = COALESCE(deleted_by_user_id, @byUserId)", new { id, byUserId }, ct);

    public Task SetContactAsync(long id, long? contactId, CancellationToken ct = default) => UpdateAsync("contact_id = @contactId", new { id, contactId }, ct);

    public async Task<bool?> ContactIsActiveAsync(long contactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool?>(new CommandDefinition("SELECT is_active FROM contact WHERE contact_id = @contactId", new { contactId }, cancellationToken: ct));
    }

    private async Task UpdateAsync(string set, object args, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE presentation_link SET " + set + ", updated_utc = UTC_TIMESTAMP(3) WHERE presentation_link_id = @id", args, cancellationToken: ct));
    }
}
