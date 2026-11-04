using Dapper;
using Nadlan.Core.Work;

namespace Nadlan.Persistence.MySql.Work;

/// <summary>The contractor's link (migration 024): one per contractor, all his open requests.</summary>
public sealed partial class MySqlWorkStore
{
    private const string ContractorLinkSelect = """
        SELECT l.contact_id, c.display_name AS contractor_name, l.link_token_hash, l.link_token_sealed, l.link_enabled, l.link_expires_utc,
               l.password_hash, l.password_version, l.failed_unlocks, l.locked_until_utc
        FROM work_contractor_link l
        JOIN contact c ON c.contact_id = l.contact_id
        """;

    public async Task<WorkContractorLink?> GetContractorLinkAsync(long contactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkContractorLink>(new CommandDefinition(
            ContractorLinkSelect + " WHERE l.contact_id = @contactId", new { contactId }, cancellationToken: ct));
    }

    public async Task<WorkContractorLink?> GetContractorLinkByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkContractorLink>(new CommandDefinition(
            ContractorLinkSelect + " WHERE l.link_token_hash = @tokenHash", new { tokenHash }, cancellationToken: ct));
    }

    public async Task AddContractorLinkAsync(long contactId, string tokenHash, string tokenSealed, DateTime expiresUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        // Two staff at once: the first one made stays.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT IGNORE INTO work_contractor_link (contact_id, link_token_hash, link_token_sealed, link_expires_utc)
            VALUES (@contactId, @tokenHash, @tokenSealed, @expiresUtc)
            """, new { contactId, tokenHash, tokenSealed, expiresUtc }, cancellationToken: ct));
    }

    public Task SetContractorLinkEnabledAsync(long contactId, bool enabled, CancellationToken ct = default)
        => UpdateContractorLinkAsync("link_enabled = @enabled", new { contactId, enabled }, ct);

    public Task SetContractorLinkExpiryAsync(long contactId, DateTime expiresUtc, CancellationToken ct = default)
        => UpdateContractorLinkAsync("link_expires_utc = @expiresUtc", new { contactId, expiresUtc }, ct);

    /// <summary>A new address: the old one stops at once; the password stays (phones that gave it must open the new address).</summary>
    public Task SetContractorLinkTokenAsync(long contactId, string tokenHash, string tokenSealed, CancellationToken ct = default)
        => UpdateContractorLinkAsync("link_token_hash = @tokenHash, link_token_sealed = @tokenSealed", new { contactId, tokenHash, tokenSealed }, ct);

    public Task SetContractorPasswordAsync(long contactId, string? passwordHash, CancellationToken ct = default)
        => UpdateContractorLinkAsync("password_hash = @passwordHash, password_version = password_version + 1, failed_unlocks = 0, locked_until_utc = NULL",
            new { contactId, passwordHash }, ct);

    public async Task<int> CountWrongContractorUnlockAsync(long contactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            UPDATE work_contractor_link SET failed_unlocks = failed_unlocks + 1 WHERE contact_id = @contactId;
            SELECT failed_unlocks FROM work_contractor_link WHERE contact_id = @contactId;
            """, new { contactId }, cancellationToken: ct));
    }

    public async Task LockContractorLinkAsync(long contactId, DateTime lockedUntilUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_contractor_link SET failed_unlocks = 0, locked_until_utc = @lockedUntilUtc WHERE contact_id = @contactId",
            new { contactId, lockedUntilUtc }, cancellationToken: ct));
    }

    public async Task ResetContractorUnlocksAsync(long contactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_contractor_link SET failed_unlocks = 0, locked_until_utc = NULL WHERE contact_id = @contactId",
            new { contactId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WorkRequest>> ListOpenRequestsOfContractorAsync(long contactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkRequest>(new CommandDefinition(RequestSelect + """
             WHERE r.contractor_contact_id = @contactId
               AND EXISTS (SELECT 1 FROM work_item wi WHERE wi.work_request_id = r.work_request_id AND wi.status <> 'PAID')
             ORDER BY r.created_utc DESC, r.work_request_id DESC
            """, new { contactId }, cancellationToken: ct))).ToList();
    }

    public async Task<long?> GetItemRequestIdAsync(long workItemId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT work_request_id FROM work_item WHERE work_item_id = @workItemId", new { workItemId }, cancellationToken: ct));
    }

    public async Task<long?> GetFileRequestIdAsync(long workFileId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT wi.work_request_id FROM work_file f JOIN work_item wi ON wi.work_item_id = f.work_item_id WHERE f.work_file_id = @workFileId
            """, new { workFileId }, cancellationToken: ct));
    }

    private async Task UpdateContractorLinkAsync(string set, object args, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_contractor_link SET " + set + ", updated_utc = UTC_TIMESTAMP(3) WHERE contact_id = @contactId", args, cancellationToken: ct));
    }
}
