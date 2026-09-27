using Dapper;
using MySqlConnector;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Persistence.MySql.Security;

public sealed class MySqlRoleStore : IRoleStore
{
    private readonly MySqlDatabase _db;

    public MySqlRoleStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SecurityRole>> ListRolesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var roles = (await conn.QueryAsync<RoleRow>(new CommandDefinition("""
            SELECT r.security_role_id, r.code, r.name, r.description, r.is_system, r.is_active, r.sort_order,
                   (SELECT COUNT(*) FROM app_user u WHERE u.security_role_id = r.security_role_id) AS user_count
            FROM security_role r
            ORDER BY r.sort_order, r.name
            """, cancellationToken: ct))).ToList();
        var perms = (await conn.QueryAsync<(int RoleId, string Code)>(new CommandDefinition("""
            SELECT rp.security_role_id AS RoleId, p.code AS Code
            FROM role_permission rp JOIN permission p ON p.permission_id = rp.permission_id
            ORDER BY p.sort_order
            """, cancellationToken: ct))).ToLookup(x => x.RoleId, x => x.Code);
        return roles.Select(r => r.ToRole(perms[r.SecurityRoleId].ToList())).ToList();
    }

    public async Task<SecurityRole?> GetRoleAsync(int securityRoleId, CancellationToken ct = default)
        => (await ListRolesAsync(ct)).FirstOrDefault(r => r.SecurityRoleId == securityRoleId);

    public async Task<IReadOnlySet<string>> GetRolePermissionCodesAsync(int securityRoleId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var codes = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT p.code FROM role_permission rp JOIN permission p ON p.permission_id = rp.permission_id
            JOIN security_role r ON r.security_role_id = rp.security_role_id
            WHERE rp.security_role_id = @securityRoleId AND r.is_active = 1
            """, new { securityRoleId }, cancellationToken: ct));
        return codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<PermissionInfo>> ListPermissionsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<PermissionRow>(new CommandDefinition("""
            SELECT permission_id, code, name, description, scope, resource_type, group_name, sort_order
            FROM permission ORDER BY sort_order, code
            """, cancellationToken: ct));
        return rows.Select(p => new PermissionInfo(p.PermissionId, p.Code, p.Name, p.Description, p.Scope, p.ResourceType, p.GroupName, p.SortOrder)).ToList();
    }

    public async Task SetRolePermissionsAsync(int securityRoleId, IReadOnlyList<int> permissionIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM role_permission WHERE security_role_id = @securityRoleId",
            new { securityRoleId }, tx, cancellationToken: ct));
        foreach (var permissionId in permissionIds)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO role_permission (security_role_id, permission_id) VALUES (@securityRoleId, @permissionId)",
                new { securityRoleId, permissionId }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<int> InsertRoleAsync(string code, string name, string? description, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        try
        {
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                INSERT INTO security_role (code, name, description, sort_order)
                SELECT @code, @name, @description, COALESCE(MAX(sort_order), 0) + 10 FROM security_role;
                SELECT LAST_INSERT_ID();
                """, new { code, name, description }, cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new DuplicateKeyException($"Role {code} already exists.", ex);
        }
    }

    public async Task UpdateRoleAsync(int securityRoleId, string name, string? description, bool isActive, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE security_role SET name = @name, description = @description, is_active = @isActive WHERE security_role_id = @securityRoleId",
            new { securityRoleId, name, description, isActive }, cancellationToken: ct));
    }

    private sealed class RoleRow
    {
        public int SecurityRoleId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public bool IsSystem { get; init; }
        public bool IsActive { get; init; }
        public int SortOrder { get; init; }
        public long UserCount { get; init; }

        public SecurityRole ToRole(IReadOnlyList<string> codes) => new(SecurityRoleId, Code, Name, Description, IsSystem, IsActive, SortOrder, codes, UserCount);
    }

    private sealed class PermissionRow
    {
        public int PermissionId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string Scope { get; init; } = "";
        public string? ResourceType { get; init; }
        public string GroupName { get; init; } = "";
        public int SortOrder { get; init; }
    }
}
