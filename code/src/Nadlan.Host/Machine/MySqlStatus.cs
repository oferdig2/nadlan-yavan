using System.Diagnostics;
using MySqlConnector;
using Nadlan.Persistence.MySql;

namespace Nadlan.Host.Machine;

public sealed record MySqlSample(
    bool Reachable,
    string? Error,
    double? PingMs,
    string? Version,
    long? UptimeSeconds,
    long? ThreadsConnected,
    long? ThreadsRunning,
    long? MaxUsedConnections,
    long? MaxConnections,
    double? QueriesPerSecond,
    long? SlowQueries,
    double? BufferPoolMb,
    double? BufferPoolUsedPercent,
    double? BufferPoolHitPercent);

public sealed record MySqlTableSize(string Name, long? Rows, double DataMb, double IndexMb);

/// <summary>
/// The database's own view: SHOW GLOBAL STATUS / VARIABLES need no extra MySQL rights, so the app's own user can read them.
/// Queries per second is the rise of "Questions" since the previous call. Called from one thread (MachineMonitor).
/// </summary>
public sealed class MySqlStatus
{
    private static readonly string[] StatusNames =
    {
        "Uptime", "Threads_connected", "Threads_running", "Max_used_connections", "Questions", "Slow_queries",
        "Innodb_buffer_pool_pages_total", "Innodb_buffer_pool_pages_free", "Innodb_buffer_pool_read_requests", "Innodb_buffer_pool_reads",
    };

    private readonly MySqlDatabase _db;
    private (long Questions, long Uptime)? _previous;

    public MySqlStatus(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<MySqlSample> ReadAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using (var ping = new MySqlCommand("SELECT 1", conn))
            {
                await ping.ExecuteScalarAsync(ct);
            }

            var pingMs = watch.Elapsed.TotalMilliseconds;
            var status = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new MySqlCommand(
                $"SHOW GLOBAL STATUS WHERE Variable_name IN ({string.Join(",", StatusNames.Select(n => $"'{n}'"))})", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    if (long.TryParse(r.GetString(1), out var n))
                    {
                        status[r.GetString(0)] = n;
                    }
                }
            }

            string? version = null;
            long? maxConnections = null, bufferPoolBytes = null;
            await using (var cmd = new MySqlCommand("SELECT @@version, @@max_connections, @@innodb_buffer_pool_size", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (await r.ReadAsync(ct))
                {
                    version = r.GetString(0);
                    maxConnections = Convert.ToInt64(r.GetValue(1));
                    bufferPoolBytes = Convert.ToInt64(r.GetValue(2));
                }
            }

            long? Get(string name) => status.TryGetValue(name, out var n) ? n : null;
            double? qps = null;
            if (Get("Questions") is { } questions && Get("Uptime") is { } uptime)
            {
                if (_previous is { } prev && uptime > prev.Uptime && questions >= prev.Questions)
                {
                    qps = (questions - prev.Questions) / (double)(uptime - prev.Uptime);
                }

                _previous = (questions, uptime);
            }

            double? usedPercent = Get("Innodb_buffer_pool_pages_total") is > 0 and var total && Get("Innodb_buffer_pool_pages_free") is { } free
                ? 100.0 * (total - free) / total
                : null;
            double? hitPercent = Get("Innodb_buffer_pool_read_requests") is > 0 and var requests && Get("Innodb_buffer_pool_reads") is { } diskReads
                ? 100.0 * (1 - (double)diskReads / requests)
                : null;

            return new MySqlSample(true, null, pingMs, version, Get("Uptime"), Get("Threads_connected"), Get("Threads_running"),
                Get("Max_used_connections"), maxConnections, qps, Get("Slow_queries"),
                bufferPoolBytes / 1024.0 / 1024, usedPercent, hitPercent);
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException)
        {
            _previous = null; // a restart resets the counters
            return new MySqlSample(false, ex.Message, null, null, null, null, null, null, null, null, null, null, null, null);
        }
    }

    /// <summary>The app database's tables by size. information_schema caches these figures, so they are approximate.</summary>
    public async Task<IReadOnlyList<MySqlTableSize>> ReadTablesAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new MySqlCommand("""
            SELECT table_name, table_rows, COALESCE(data_length, 0), COALESCE(index_length, 0)
            FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'
            ORDER BY COALESCE(data_length, 0) + COALESCE(index_length, 0) DESC
            """, conn);
        var result = new List<MySqlTableSize>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new MySqlTableSize(r.GetString(0), r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1)),
                Convert.ToInt64(r.GetValue(2)) / 1024.0 / 1024, Convert.ToInt64(r.GetValue(3)) / 1024.0 / 1024));
        }

        return result;
    }
}
