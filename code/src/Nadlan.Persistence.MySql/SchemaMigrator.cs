using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace Nadlan.Persistence.MySql;

public sealed record SchemaStatus(bool DatabaseExists, int CurrentVersion, int LatestVersion, IReadOnlyList<string> Pending)
{
    public bool IsUpToDate => DatabaseExists && Pending.Count == 0;
}

/// <summary>
/// Applies embedded Sql/NNN_name.sql scripts in order and records each in schema_version.
/// Only the DB tool (Nadlan.DbTool, via update-db.ps1) applies changes; the app only checks <see cref="GetStatusAsync"/>.
///
/// MySQL DDL auto-commits, so a script that fails halfway is not rolled back. Keep each script small and
/// fix forward (a new script) rather than editing one that has already been applied anywhere.
/// </summary>
public sealed partial class SchemaMigrator
{
    private const int UnknownDatabaseError = 1049;

    private readonly MySqlDatabase _db;

    public SchemaMigrator(MySqlDatabase db)
    {
        _db = db;
    }

    /// <summary>Read-only: never creates or changes anything.</summary>
    public async Task<SchemaStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var scripts = LoadScripts().ToList();
        var latest = scripts.Count == 0 ? 0 : scripts[^1].Version;
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            var current = await TableExistsAsync(conn, "schema_version", ct) ? await GetCurrentVersionAsync(conn, ct) : 0;
            return new SchemaStatus(true, current, latest, scripts.Where(s => s.Version > current).Select(s => s.Name).ToList());
        }
        catch (MySqlException ex) when (ex.Number == UnknownDatabaseError)
        {
            return new SchemaStatus(false, 0, latest, scripts.Select(s => s.Name).ToList());
        }
    }

    /// <summary>Throws with an actionable message unless the schema is fully migrated.</summary>
    public async Task EnsureUpToDateAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);
        if (!status.IsUpToDate)
        {
            var what = status.DatabaseExists
                ? $"is at version {status.CurrentVersion}; pending: {string.Join(", ", status.Pending)}"
                : "does not exist";
            throw new InvalidOperationException($"Database '{_db.DatabaseName}' {what}. Run update-db.ps1 first.");
        }
    }

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseExistsAsync(ct);

        await using var conn = await _db.OpenAsync(ct);
        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS schema_version (
              version INT NOT NULL,
              name VARCHAR(200) NOT NULL,
              applied_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
              PRIMARY KEY (version)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """, ct);

        var current = await GetCurrentVersionAsync(conn, ct);
        var applied = new List<string>();
        foreach (var script in LoadScripts().Where(s => s.Version > current))
        {
            var statements = SplitStatements(script.Sql);
            for (var i = 0; i < statements.Count; i++)
            {
                try
                {
                    await ExecuteAsync(conn, statements[i], ct);
                }
                catch (MySqlException ex)
                {
                    var partial = i > 0
                        ? $" Statements 1-{i} of that script already ran, so the schema is half-updated (MySQL can't roll back DDL)." +
                          " On a local dev database, rebuild it with: update-db.ps1 -Reset"
                        : "";
                    throw new InvalidOperationException(
                        $"Migration {script.Name} failed at statement {i + 1} of {statements.Count}: {ex.Message}.{partial}", ex);
                }
            }

            await using var cmd = new MySqlCommand("INSERT INTO schema_version (version, name) VALUES (@v, @n)", conn);
            cmd.Parameters.AddWithValue("@v", script.Version);
            cmd.Parameters.AddWithValue("@n", script.Name);
            await cmd.ExecuteNonQueryAsync(ct);
            applied.Add(script.Name);
        }

        return applied;
    }

    /// <summary>
    /// DEV ONLY: drops the whole schema (all data!) and rebuilds it from the scripts. The caller must pass the
    /// database name back as confirmation, so a wrong connection string can't wipe another database by accident.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResetAsync(string confirmDatabaseName, CancellationToken ct = default)
    {
        if (!string.Equals(confirmDatabaseName, _db.DatabaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Reset not confirmed: expected '{_db.DatabaseName}', got '{confirmDatabaseName}'.");
        }

        EnsureSafeName();
        await using (var conn = await _db.OpenServerAsync(ct))
        {
            await ExecuteAsync(conn, $"DROP DATABASE IF EXISTS `{_db.DatabaseName}`", ct);
        }

        MySqlConnection.ClearAllPools(); // pooled connections still point at the dropped schema
        return await MigrateAsync(ct);
    }

    private void EnsureSafeName()
    {
        if (!SafeIdentifier().IsMatch(_db.DatabaseName))
        {
            throw new InvalidOperationException($"Database name '{_db.DatabaseName}' contains unsupported characters.");
        }
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        EnsureSafeName();
        await using var conn = await _db.OpenServerAsync(ct);
        await ExecuteAsync(conn, $"CREATE DATABASE IF NOT EXISTS `{_db.DatabaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci", ct);
    }

    private static async Task<bool> TableExistsAsync(MySqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t", conn);
        cmd.Parameters.AddWithValue("@t", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<int> GetCurrentVersionAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT COALESCE(MAX(version), 0) FROM schema_version", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ExecuteAsync(MySqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record Script(int Version, string Name, string Sql);

    private static IEnumerable<Script> LoadScripts()
    {
        var asm = Assembly.GetExecutingAssembly();
        var scripts = new List<Script>();
        foreach (var resource in asm.GetManifestResourceNames())
        {
            var match = ScriptName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            scripts.Add(new Script(int.Parse(match.Groups["v"].Value), match.Groups["name"].Value, reader.ReadToEnd()));
        }

        var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Two migration scripts share version {duplicate.Key}.");
        }

        return scripts.OrderBy(s => s.Version);
    }

    /// <summary>
    /// Splits a script into statements on ';', ignoring ';' inside '...', "...", `...` and "-- " comments
    /// (a COMMENT 'a; b' must not end the statement). Comments are dropped. Scripts must not contain procedures.
    /// </summary>
    internal static IReadOnlyList<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (quote is not null)
            {
                current.Append(c);
                if (c == '\\' && quote != '`' && i + 1 < sql.Length)
                {
                    current.Append(sql[++i]); // backslash escape inside a string
                }
                else if (c == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        current.Append(sql[++i]); // doubled quote = literal quote
                    }
                    else
                    {
                        quote = null;
                    }
                }

                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++; // skip comment to end of line
                }

                current.Append('\n');
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                current.Append(c);
            }
            else if (c == ';')
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }

        if (quote is not null)
        {
            throw new InvalidOperationException("Migration script has an unclosed quote.");
        }

        Flush();
        return statements;

        void Flush()
        {
            var statement = current.ToString().Trim();
            if (statement.Length > 0)
            {
                statements.Add(statement);
            }

            current.Clear();
        }
    }

    /// <summary>For tests: every embedded script, split into statements.</summary>
    internal static IEnumerable<(string Script, IReadOnlyList<string> Statements)> SplitAllScripts()
        => LoadScripts().Select(s => (s.Name, SplitStatements(s.Sql)));

    [GeneratedRegex(@"\.Sql\.(?<name>(?<v>\d{3})_[A-Za-z0-9_]+)\.sql$")]
    private static partial Regex ScriptName();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex SafeIdentifier();
}
