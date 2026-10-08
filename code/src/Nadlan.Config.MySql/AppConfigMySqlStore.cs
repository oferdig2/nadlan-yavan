using MySqlConnector;

namespace Nadlan.Config.MySql;

/// <summary>Reads/writes app_config rows. The table itself is created by the DB tool (migration 001).</summary>
public sealed class AppConfigMySqlStore
{
    private readonly string _connectionString;

    public AppConfigMySqlStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<(bool Found, string JsonText, long UpdatedUtcMs)> TryGetAsync(string configKey, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(
            "SELECT json_text, updated_utc_ms FROM app_config WHERE config_key = @config_key LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@config_key", configKey);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (true, r.GetString(0), r.GetInt64(1)) : (false, "", 0);
    }

    public async Task<IReadOnlyList<(string ConfigKey, long UpdatedUtcMs)>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("SELECT config_key, updated_utc_ms FROM app_config ORDER BY config_key", conn);
        var result = new List<(string, long)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add((r.GetString(0), r.GetInt64(1)));
        }

        return result;
    }


    /// <summary>
    /// Replaces a row only if it is still the version the editor loaded (<paramref name="expectedUpdatedUtcMs"/>; 0 = the
    /// row must not exist yet). The version it replaces goes to app_config_history in the same transaction
    /// (<paramref name="replacedBy"/> says who). Returns the new version, or null when someone else saved in between.
    /// </summary>
    public async Task<long?> TryReplaceAsync(string configKey, string jsonText, long expectedUpdatedUtcMs, string? replacedBy = null, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        // Strictly newer than the version it replaces, even within the same millisecond.
        var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), expectedUpdatedUtcMs + 1);
        if (expectedUpdatedUtcMs == 0)
        {
            await using var insert = new MySqlCommand(
                "INSERT IGNORE INTO app_config (config_key, json_text, updated_utc_ms) VALUES (@config_key, @json_text, @now)", conn);
            insert.Parameters.AddWithValue("@config_key", configKey);
            insert.Parameters.AddWithValue("@json_text", jsonText);
            insert.Parameters.AddWithValue("@now", now);
            return await insert.ExecuteNonQueryAsync(ct) == 1 ? now : null;
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        var previous = await ReadForUpdateAsync(conn, tx, configKey, ct);
        if (previous is null || previous.Value.UpdatedUtcMs != expectedUpdatedUtcMs)
        {
            return null; // rolled back on dispose
        }

        await KeepPreviousAsync(conn, tx, configKey, previous.Value.Json, previous.Value.UpdatedUtcMs, replacedBy, ct);
        await WriteAsync(conn, tx, configKey, jsonText, now, ct);
        await tx.CommitAsync(ct);
        return now;
    }

    /// <summary>Writes a row whatever version it has (tools, startup defaults); the version it replaces is kept in history.</summary>
    public async Task UpsertAsync(string configKey, string jsonText, string? replacedBy = null, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var previous = await ReadForUpdateAsync(conn, tx, configKey, ct);
        var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (previous?.UpdatedUtcMs ?? 0) + 1);
        if (previous is { } p)
        {
            if (p.Json == jsonText)
            {
                return; // nothing changes: no new version, no history row
            }

            await KeepPreviousAsync(conn, tx, configKey, p.Json, p.UpdatedUtcMs, replacedBy, ct);
        }

        await WriteAsync(conn, tx, configKey, jsonText, now, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>The kept versions of a row, newest first: id, its version, when it was replaced and by whom.</summary>
    public async Task<IReadOnlyList<AppConfigHistoryEntry>> ListHistoryAsync(string configKey, int limit, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("""
            SELECT history_id, json_text, version_utc_ms, replaced_utc, replaced_by
            FROM app_config_history WHERE config_key = @config_key ORDER BY history_id DESC LIMIT @limit
            """, conn);
        cmd.Parameters.AddWithValue("@config_key", configKey);
        cmd.Parameters.AddWithValue("@limit", limit);
        var result = new List<AppConfigHistoryEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new AppConfigHistoryEntry(r.GetInt64(0), r.GetString(1), r.GetInt64(2),
                DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc), r.IsDBNull(4) ? null : r.GetString(4)));
        }

        return result;
    }

    /// <summary>
    /// Puts a kept version back (<paramref name="historyId"/>; null = the newest, i.e. undo the last change). The version
    /// it replaces is kept too, so a restore can itself be undone. Returns the restored entry, or null if there is none.
    /// </summary>
    public async Task<AppConfigHistoryEntry?> RestoreAsync(string configKey, long? historyId, string? replacedBy, CancellationToken ct = default)
    {
        var entry = historyId is long id
            ? (await ListHistoryAsync(configKey, int.MaxValue, ct)).FirstOrDefault(h => h.HistoryId == id)
            : (await ListHistoryAsync(configKey, 1, ct)).FirstOrDefault();
        if (entry is null)
        {
            return null;
        }

        await UpsertAsync(configKey, entry.Json, replacedBy, ct);
        return entry;
    }

    private static async Task<(string Json, long UpdatedUtcMs)?> ReadForUpdateAsync(MySqlConnection conn, MySqlTransaction tx, string configKey, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT json_text, updated_utc_ms FROM app_config WHERE config_key = @config_key FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("@config_key", configKey);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0), r.GetInt64(1)) : null;
    }

    private static async Task WriteAsync(MySqlConnection conn, MySqlTransaction tx, string configKey, string jsonText, long now, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO app_config (config_key, json_text, updated_utc_ms) VALUES (@config_key, @json_text, @now)
            ON DUPLICATE KEY UPDATE json_text = VALUES(json_text), updated_utc_ms = VALUES(updated_utc_ms)
            """, conn, tx);
        cmd.Parameters.AddWithValue("@config_key", configKey);
        cmd.Parameters.AddWithValue("@json_text", jsonText);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task KeepPreviousAsync(MySqlConnection conn, MySqlTransaction tx, string configKey, string json, long versionUtcMs, string? replacedBy,
        CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO app_config_history (config_key, json_text, version_utc_ms, replaced_by)
            VALUES (@config_key, @json_text, @version, @replaced_by)
            """, conn, tx);
        cmd.Parameters.AddWithValue("@config_key", configKey);
        cmd.Parameters.AddWithValue("@json_text", json);
        cmd.Parameters.AddWithValue("@version", versionUtcMs);
        cmd.Parameters.AddWithValue("@replaced_by", replacedBy is null ? DBNull.Value : replacedBy.Length <= 200 ? replacedBy : replacedBy[..200]);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            // A database from before migration 015 (a tool run before "migrate"): the change itself still goes through.
        }
    }
}

/// <summary>A kept version of an app_config row (app_config_history).</summary>
public sealed record AppConfigHistoryEntry(long HistoryId, string Json, long VersionUtcMs, DateTime ReplacedUtc, string? ReplacedBy);
