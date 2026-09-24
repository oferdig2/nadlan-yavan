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

    public async Task UpsertAsync(string configKey, string jsonText, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("""
            INSERT INTO app_config (config_key, json_text, updated_utc_ms)
            VALUES (@config_key, @json_text, @updated_utc_ms)
            ON DUPLICATE KEY UPDATE json_text = VALUES(json_text), updated_utc_ms = VALUES(updated_utc_ms)
            """, conn);
        cmd.Parameters.AddWithValue("@config_key", configKey);
        cmd.Parameters.AddWithValue("@json_text", jsonText);
        cmd.Parameters.AddWithValue("@updated_utc_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
