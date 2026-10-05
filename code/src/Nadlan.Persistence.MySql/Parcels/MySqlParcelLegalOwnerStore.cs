using Dapper;
using Nadlan.Core.Parcels;

namespace Nadlan.Persistence.MySql.Parcels;

public sealed class MySqlParcelLegalOwnerStore : IParcelLegalOwnerStore
{
    private readonly MySqlDatabase _db;

    public MySqlParcelLegalOwnerStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LegalOwner>> ListAsync(long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<LegalOwner>(new CommandDefinition("""
            SELECT o.parcel_id AS ParcelId, o.contact_id AS ContactId, c.display_name AS DisplayName,
                   o.ownership_percent AS OwnershipPercent, o.notes AS Notes
            FROM parcel_legal_owner o
            JOIN contact c ON c.contact_id = o.contact_id
            WHERE o.parcel_id = @parcelId
            ORDER BY o.ownership_percent DESC, c.display_name
            """, new { parcelId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<decimal?> UpsertAsync(long parcelId, long contactId, decimal? ownershipPercent, string? notes, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // The Parcel row is the lock every owner change on this Parcel waits for.
        await conn.ExecuteAsync(new CommandDefinition("SELECT parcel_id FROM parcel WHERE parcel_id = @parcelId FOR UPDATE",
            new { parcelId }, tx, cancellationToken: ct));
        var others = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(ownership_percent), 0) FROM parcel_legal_owner WHERE parcel_id = @parcelId AND contact_id <> @contactId
            """, new { parcelId, contactId }, tx, cancellationToken: ct));
        var total = others + (ownershipPercent ?? 0);
        if (total > 100)
        {
            return total;
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO parcel_legal_owner (parcel_id, contact_id, ownership_percent, notes)
            VALUES (@parcelId, @contactId, @ownershipPercent, @notes)
            ON DUPLICATE KEY UPDATE ownership_percent = VALUES(ownership_percent), notes = VALUES(notes),
                                    updated_utc = UTC_TIMESTAMP(3)
            """, new { parcelId, contactId, ownershipPercent, notes }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return null;
    }

    public async Task<bool> RemoveAsync(long parcelId, long contactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM parcel_legal_owner WHERE parcel_id = @parcelId AND contact_id = @contactId",
            new { parcelId, contactId }, cancellationToken: ct)) > 0;
    }
}
