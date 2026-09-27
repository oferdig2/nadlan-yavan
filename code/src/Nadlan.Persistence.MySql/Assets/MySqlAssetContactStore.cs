using Dapper;
using MySqlConnector;
using Nadlan.Core.Assets;

namespace Nadlan.Persistence.MySql.Assets;

public sealed class MySqlAssetContactStore : IAssetContactStore
{
    private const string Select = """
        SELECT l.asset_contact_id AS AssetContactId, l.asset_id AS AssetId, l.contact_id AS ContactId,
               c.display_name AS DisplayName, COALESCE(c.cell_phone, c.phone) AS Phone, c.email AS Email,
               l.relationship_type AS RelationshipType, l.notes AS Notes
        FROM asset_contact l
        JOIN contact c ON c.contact_id = l.contact_id
        """;

    private readonly MySqlDatabase _db;

    public MySqlAssetContactStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<AssetContactLink>> ListAsync(long assetId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<AssetContactLink>(new CommandDefinition(
            $"{Select} WHERE l.asset_id = @assetId ORDER BY l.relationship_type, c.display_name", new { assetId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<AssetContactLink?> GetAsync(long assetContactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AssetContactLink>(new CommandDefinition(
            $"{Select} WHERE l.asset_contact_id = @assetContactId", new { assetContactId }, cancellationToken: ct));
    }

    public async Task<long?> InsertAsync(long assetId, long contactId, string relationshipType, string? notes, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO asset_contact (asset_id, contact_id, relationship_type, notes)
                VALUES (@assetId, @contactId, @relationshipType, @notes);
                SELECT LAST_INSERT_ID();
                """, new { assetId, contactId, relationshipType, notes }, cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return null; // same Contact already has this role on the Asset
        }
    }

    public async Task<bool> DeleteAsync(long assetContactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM asset_contact WHERE asset_contact_id = @assetContactId", new { assetContactId }, cancellationToken: ct)) > 0;
    }
}
