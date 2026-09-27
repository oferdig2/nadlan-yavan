using Dapper;
using MySqlConnector;
using Nadlan.Core.Reference;
using Nadlan.Core.Validation;

namespace Nadlan.Persistence.MySql.Reference;

public sealed class MySqlReferenceAdminStore : IReferenceAdminStore
{
    private sealed record Table(string Name, string IdColumn, string? ColorColumn = null, string? CategoryColumn = null, bool HasCountry = false);

    // Fixed map: table/column names never come from input.
    private static readonly Dictionary<ReferenceTable, Table> Tables = new()
    {
        [ReferenceTable.AssetStatus] = new("asset_status", "asset_status_id", ColorColumn: "map_color"),
        [ReferenceTable.PropertyType] = new("property_type", "property_type_id"),
        [ReferenceTable.PortfolioType] = new("portfolio_type", "portfolio_type_id"),
        [ReferenceTable.ContactRole] = new("contact_role", "contact_role_id"),
        [ReferenceTable.FileType] = new("file_type", "file_type_id", CategoryColumn: "category"),
        [ReferenceTable.GeographicArea] = new("geographic_area", "geographic_area_id", HasCountry: true),
    };

    private readonly MySqlDatabase _db;

    public MySqlReferenceAdminStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ReferenceRow>> ListAsync(ReferenceTable table, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<ReferenceRow>(new CommandDefinition(
            $"{Select(Tables[table])} ORDER BY sort_order, name", cancellationToken: ct))).ToList();
    }

    public async Task<ReferenceRow?> GetAsync(ReferenceTable table, int id, CancellationToken ct = default)
    {
        var t = Tables[table];
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ReferenceRow>(new CommandDefinition(
            $"{Select(t)} WHERE {t.IdColumn} = @id", new { id }, cancellationToken: ct));
    }

    public async Task<int> InsertAsync(ReferenceTable table, ReferenceRow row, CancellationToken ct = default)
    {
        var t = Tables[table];
        var columns = new List<string> { "code", "name", "is_active", "sort_order" };
        var values = new List<string> { "@Code", "@Name", "@IsActive", "@SortOrder" };
        if (t.ColorColumn is not null) { columns.Add(t.ColorColumn); values.Add("@Color"); }
        if (t.CategoryColumn is not null) { columns.Add(t.CategoryColumn); values.Add("@Category"); }
        if (t.HasCountry) { columns.Add("country_id"); values.Add("(SELECT country_id FROM country WHERE code = 'GR')"); } // Phase 1: Greece

        try
        {
            await using var conn = await _db.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"INSERT INTO {t.Name} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)}); SELECT LAST_INSERT_ID();",
                row, cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new DuplicateKeyException($"Code {row.Code} already exists in {t.Name}.", ex);
        }
    }

    public async Task UpdateAsync(ReferenceTable table, ReferenceRow row, CancellationToken ct = default)
    {
        var t = Tables[table];
        var sets = new List<string> { "name = @Name", "is_active = @IsActive", "sort_order = @SortOrder" };
        if (t.ColorColumn is not null) { sets.Add($"{t.ColorColumn} = @Color"); }
        if (t.CategoryColumn is not null) { sets.Add($"{t.CategoryColumn} = @Category"); }
        if (t.HasCountry) { sets.Add("updated_utc = UTC_TIMESTAMP(3)"); }

        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE {t.Name} SET {string.Join(", ", sets)} WHERE {t.IdColumn} = @Id", row, cancellationToken: ct));
    }

    private static string Select(Table t) => $"""
        SELECT {t.IdColumn} AS Id, code AS Code, name AS Name, is_active AS IsActive, sort_order AS SortOrder,
               {(t.ColorColumn is null ? "CAST(NULL AS CHAR(7))" : t.ColorColumn)} AS Color,
               {(t.CategoryColumn is null ? "CAST(NULL AS CHAR(20))" : t.CategoryColumn)} AS Category
        FROM {t.Name}
        """;
}
