using Dapper;
using MySqlConnector;
using Nadlan.Core.Editing;
using Nadlan.Core.Validation;

namespace Nadlan.Persistence.MySql.Editing;

/// <summary>Version = the row's updated_utc (to the millisecond); lock = a MySQL named lock per object.</summary>
public sealed class MySqlEditVersionStore : IEditVersionStore
{
    private static readonly Dictionary<string, (string Table, string Key)> Tables = new()
    {
        [EditTargets.Asset] = ("asset", "asset_id"),
        [EditTargets.Contact] = ("contact", "contact_id"),
        [EditTargets.Portfolio] = ("portfolio", "portfolio_id"),
        [EditTargets.Parcel] = ("parcel", "parcel_id"),
    };

    private readonly MySqlDatabase _db;

    public MySqlEditVersionStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<string?> GetAsync(string target, long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await ReadAsync(conn, target, id, ct);
    }

    public async Task<IAsyncDisposable> BeginEditAsync(string target, long id, string? expectedVersion, CancellationToken ct = default)
    {
        var conn = await _db.OpenAsync(ct);
        var name = $"nadlan.edit.{target}.{id}";
        try
        {
            if (await conn.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT GET_LOCK(@name, 20)", new { name }, cancellationToken: ct)) != 1)
            {
                throw new DomainValidationException("EDIT_BUSY", $"This {target} is being saved by someone else right now. Try again in a moment.");
            }

            var lease = new Lease(conn, name);
            if (!string.IsNullOrEmpty(expectedVersion) && await ReadAsync(conn, target, id, ct) is { } current && current != expectedVersion)
            {
                await lease.DisposeAsync();
                throw new EditConflictException($"This {target}");
            }

            return lease;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static Task<string?> ReadAsync(MySqlConnection conn, string target, long id, CancellationToken ct)
    {
        var (table, key) = Tables.TryGetValue(target, out var t) ? t : throw new ArgumentOutOfRangeException(nameof(target), target, null);
        return conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT DATE_FORMAT(updated_utc, '%Y%m%d%H%i%s%f') FROM {table} WHERE {key} = @id", new { id }, cancellationToken: ct));
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly MySqlConnection _conn;
        private readonly string _name;
        private bool _done;

        public Lease(MySqlConnection conn, string name)
        {
            _conn = conn;
            _name = name;
        }

        public async ValueTask DisposeAsync()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            try
            {
                await _conn.ExecuteAsync("DO RELEASE_LOCK(@name)", new { name = _name });
            }
            catch (MySqlException)
            {
                // Closing the connection releases it too.
            }
            finally
            {
                await _conn.DisposeAsync();
            }
        }
    }
}
