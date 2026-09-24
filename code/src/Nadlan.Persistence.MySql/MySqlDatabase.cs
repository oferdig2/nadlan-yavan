using MySqlConnector;

namespace Nadlan.Persistence.MySql;

/// <summary>Connection factory for the Nadlan application database.</summary>
public sealed class MySqlDatabase
{
    /// <summary>Bootstrap env var holding the only secret the app needs to start (see start-nadlan.ps1).</summary>
    public const string ConnectionStringEnvVar = "NADLAN_MYSQL_CS";

    public string ConnectionString { get; }
    public string DatabaseName { get; }

    static MySqlDatabase()
    {
        // Lets Dapper map snake_case columns (parcel_id) onto PascalCase properties (ParcelId).
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public MySqlDatabase(string connectionString)
    {
        var b = new MySqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(b.Database))
        {
            throw new ArgumentException("Connection string must name a database (e.g. database=nadlan).", nameof(connectionString));
        }

        DatabaseName = b.Database;
        ConnectionString = b.ConnectionString;
    }

    public static MySqlDatabase FromEnvironment()
    {
        var cs = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        if (string.IsNullOrWhiteSpace(cs))
        {
            throw new InvalidOperationException($"{ConnectionStringEnvVar} is not set. Start via start-nadlan.ps1 or set it explicitly.");
        }

        return new MySqlDatabase(cs.Trim());
    }

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>Connection to the server without selecting a database; used only to create the database.</summary>
    internal async Task<MySqlConnection> OpenServerAsync(CancellationToken ct = default)
    {
        var b = new MySqlConnectionStringBuilder(ConnectionString) { Database = string.Empty };
        var conn = new MySqlConnection(b.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
