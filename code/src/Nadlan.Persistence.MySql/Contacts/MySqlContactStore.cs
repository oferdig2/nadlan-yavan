using Dapper;
using MySqlConnector;
using Nadlan.Core.Contacts;

namespace Nadlan.Persistence.MySql.Contacts;

public sealed class MySqlContactStore : IContactStore
{
    private readonly MySqlDatabase _db;

    public MySqlContactStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<Contact?> GetAsync(long contactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var contact = await conn.QuerySingleOrDefaultAsync<Contact>(new CommandDefinition("""
            SELECT contact_id, contact_type, display_name, first_name, last_name, company_name, email, phone,
                   cell_phone, notes, is_active
            FROM contact WHERE contact_id = @contactId
            """, new { contactId }, cancellationToken: ct));
        if (contact is null)
        {
            return null;
        }

        var roleIds = await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT contact_role_id FROM contact_contact_role WHERE contact_id = @contactId", new { contactId }, cancellationToken: ct));
        return contact with { RoleIds = roleIds.ToList() };
    }

    public async Task<IReadOnlyList<ContactSummary>> SearchAsync(string? text, int? roleId, int limit, CancellationToken ct = default)
    {
        var where = new List<string> { "c.is_active = 1" };
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Name matches from the start of any word are the useful ones; email/phone are "contains".
            where.Add("(c.display_name LIKE @prefix OR c.display_name LIKE @wordPrefix OR c.email LIKE @contains OR c.phone LIKE @contains OR c.cell_phone LIKE @contains)");
            var t = SqlLike.Escape(text.Trim());
            args.Add("prefix", $"{t}%");
            args.Add("wordPrefix", $"% {t}%");
            args.Add("contains", $"%{t}%");
        }

        if (roleId is int r)
        {
            where.Add("EXISTS (SELECT 1 FROM contact_contact_role ccr WHERE ccr.contact_id = c.contact_id AND ccr.contact_role_id = @roleId)");
            args.Add("roleId", r);
        }

        args.Add("limit", limit);
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ContactSummary>(new CommandDefinition($"""
            SELECT c.contact_id AS ContactId, c.display_name AS DisplayName, c.email AS Email,
                   COALESCE(c.cell_phone, c.phone) AS Phone, c.is_active AS IsActive
            FROM contact c
            WHERE {string.Join(" AND ", where)}
            ORDER BY c.display_name
            LIMIT @limit
            """, args, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<long> InsertAsync(Contact contact, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO contact (contact_type, display_name, first_name, last_name, company_name, email, phone,
                                 cell_phone, notes, is_active)
            VALUES (@ContactType, @DisplayName, @FirstName, @LastName, @CompanyName, @Email, @Phone,
                    @CellPhone, @Notes, @IsActive);
            SELECT LAST_INSERT_ID();
            """, contact, tx, cancellationToken: ct));
        await ReplaceRolesAsync(conn, tx, id, contact.RoleIds, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task UpdateAsync(Contact contact, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE contact
            SET contact_type = @ContactType, display_name = @DisplayName, first_name = @FirstName,
                last_name = @LastName, company_name = @CompanyName, email = @Email, phone = @Phone,
                cell_phone = @CellPhone, notes = @Notes, is_active = @IsActive, updated_utc = UTC_TIMESTAMP(3)
            WHERE contact_id = @ContactId
            """, contact, tx, cancellationToken: ct));
        await ReplaceRolesAsync(conn, tx, contact.ContactId, contact.RoleIds, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task ReplaceRolesAsync(MySqlConnection conn, MySqlTransaction tx, long contactId, IReadOnlyList<int> roleIds, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM contact_contact_role WHERE contact_id = @contactId", new { contactId }, tx, cancellationToken: ct));
        foreach (var roleId in roleIds)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO contact_contact_role (contact_id, contact_role_id) VALUES (@contactId, @roleId)",
                new { contactId, roleId }, tx, cancellationToken: ct));
        }
    }
}
