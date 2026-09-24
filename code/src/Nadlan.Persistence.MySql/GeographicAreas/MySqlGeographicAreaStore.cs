using Dapper;
using Nadlan.Core.GeographicAreas;

namespace Nadlan.Persistence.MySql.GeographicAreas;

public sealed class MySqlGeographicAreaStore : IGeographicAreaStore
{
    // GeographicArea is a positional record: Dapper binds by constructor parameter name, so alias explicitly.
    private const string SelectColumns = """
        SELECT geographic_area_id AS GeographicAreaId, country_id AS CountryId, code AS Code, name AS Name,
               is_active AS IsActive
        FROM geographic_area
        """;

    private readonly MySqlDatabase _db;

    public MySqlGeographicAreaStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<GeographicArea>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<GeographicArea>(new CommandDefinition(
            $"{SelectColumns} ORDER BY sort_order, name", cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<GeographicArea?> GetByCodeAsync(int countryId, string code, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<GeographicArea>(new CommandDefinition(
            $"{SelectColumns} WHERE country_id = @countryId AND code = @code", new { countryId, code }, cancellationToken: ct));
    }

    public async Task<int> InsertAsync(int countryId, string code, string name, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO geographic_area (country_id, code, name) VALUES (@countryId, @code, @name);
            SELECT LAST_INSERT_ID();
            """, new { countryId, code, name }, cancellationToken: ct));
    }
}

public sealed class MySqlCountryStore : ICountryStore
{
    private readonly MySqlDatabase _db;

    public MySqlCountryStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<int?> GetIdByCodeAsync(string code, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT country_id FROM country WHERE code = @code", new { code }, cancellationToken: ct));
    }
}
