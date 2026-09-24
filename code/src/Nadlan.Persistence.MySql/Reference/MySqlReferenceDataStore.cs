using Dapper;
using Nadlan.Core.Reference;

namespace Nadlan.Persistence.MySql.Reference;

public sealed class MySqlReferenceDataStore : IReferenceDataStore
{
    private readonly MySqlDatabase _db;

    public MySqlReferenceDataStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ReferenceItem>> ListAsync(ReferenceList list, CancellationToken ct = default)
    {
        // Table names come from this fixed map only, never from input.
        var sql = list switch
        {
            ReferenceList.AssetStatus => Select("asset_status", "asset_status_id", "map_color"),
            ReferenceList.PropertyType => Select("property_type", "property_type_id"),
            ReferenceList.PortfolioType => Select("portfolio_type", "portfolio_type_id"),
            ReferenceList.ContactRole => Select("contact_role", "contact_role_id"),
            _ => throw new ArgumentOutOfRangeException(nameof(list)),
        };

        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<ReferenceItem>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    private static string Select(string table, string idColumn, string? colorColumn = null) => $"""
        SELECT {idColumn} AS Id, code AS Code, name AS Name, is_active AS IsActive, sort_order AS SortOrder,
               {colorColumn ?? "CAST(NULL AS CHAR(7))"} AS Color
        FROM {table}
        ORDER BY sort_order, name
        """;
}
