using Dapper;
using MySqlConnector;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Persistence.MySql.Security;

public sealed class MySqlUserStore : IUserStore
{
    private const string Select = """
        SELECT u.user_id, u.email, u.display_name, u.contact_id, c.display_name AS contact_name, u.security_role_id,
               r.code AS role_code, r.name AS role_name, u.password_hash, u.password_changed_utc, u.must_change_password,
               u.failed_login_count, u.locked_until_utc, u.is_active, u.session_version, u.last_login_utc,
               u.last_login_method, u.created_utc, u.updated_utc
        FROM app_user u
        JOIN security_role r ON r.security_role_id = u.security_role_id
        LEFT JOIN contact c ON c.contact_id = u.contact_id
        """;

    private readonly MySqlDatabase _db;

    public MySqlUserStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<AppUser?> GetAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AppUser>(new CommandDefinition($"{Select} WHERE u.user_id = @userId", new { userId }, cancellationToken: ct));
    }

    public async Task<AppUser?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AppUser>(new CommandDefinition($"{Select} WHERE u.email = @email",
            new { email = email.Trim().ToLowerInvariant() }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AppUser>> SearchAsync(string? text, bool includeInactive, int limit, CancellationToken ct = default)
    {
        var t = string.IsNullOrWhiteSpace(text) ? null : $"%{SqlLike.Escape(text.Trim())}%";
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<AppUser>(new CommandDefinition($"""
            {Select}
            WHERE (@includeInactive OR u.is_active = 1)
              AND (@t IS NULL OR u.email LIKE @t OR u.display_name LIKE @t OR c.display_name LIKE @t)
            ORDER BY u.display_name, u.email
            LIMIT @limit
            """, new { t, includeInactive, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<long> InsertAsync(AppUser user, long? createdByUserId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        try
        {
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO app_user (email, display_name, contact_id, security_role_id, password_hash, password_changed_utc,
                                      must_change_password, is_active, created_by_user_id)
                VALUES (@Email, @DisplayName, @ContactId, @SecurityRoleId, @PasswordHash,
                        IF(@PasswordHash IS NULL, NULL, UTC_TIMESTAMP(3)), @MustChangePassword, @IsActive, @createdByUserId);
                SELECT LAST_INSERT_ID();
                """, new
                {
                    user.Email, user.DisplayName, user.ContactId, user.SecurityRoleId, user.PasswordHash,
                    user.MustChangePassword, user.IsActive, createdByUserId,
                }, cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new DuplicateKeyException($"User {user.Email} or its Contact already exists.", ex);
        }
    }

    public async Task UpdateAsync(AppUser user, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE app_user
                SET email = @Email, display_name = @DisplayName, contact_id = @ContactId, security_role_id = @SecurityRoleId,
                    is_active = @IsActive, must_change_password = @MustChangePassword, updated_utc = UTC_TIMESTAMP(3)
                WHERE user_id = @UserId
                """, user, cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new DuplicateKeyException($"User {user.Email} or its Contact already exists.", ex);
        }
    }

    public async Task SetPasswordAsync(long userId, string? passwordHash, bool mustChangePassword, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE app_user
            SET password_hash = @passwordHash, password_changed_utc = IF(@passwordHash IS NULL, NULL, UTC_TIMESTAMP(3)),
                must_change_password = @mustChangePassword, failed_login_count = 0, locked_until_utc = NULL,
                session_version = session_version + 1, updated_utc = UTC_TIMESTAMP(3)
            WHERE user_id = @userId
            """, new { userId, passwordHash, mustChangePassword }, cancellationToken: ct));
    }

    public async Task BumpSessionVersionAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE app_user SET session_version = session_version + 1, updated_utc = UTC_TIMESTAMP(3) WHERE user_id = @userId",
            new { userId }, cancellationToken: ct));
    }

    public async Task<int> RecordFailedLoginAsync(long userId, int maxFailures, DateTime lockUntilUtc, CancellationToken ct = default)
    {
        // DATETIME(3) keeps milliseconds only; compare against the value MySQL actually stores.
        lockUntilUtc = new DateTime(lockUntilUtc.Ticks - lockUntilUtc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        await using var conn = await _db.OpenAsync(ct);
        // One statement, so parallel wrong guesses can't slip past the limit. The count restarts after a lock.
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            UPDATE app_user
            SET locked_until_utc = IF(failed_login_count + 1 >= @maxFailures, @lockUntilUtc, locked_until_utc),
                failed_login_count = IF(failed_login_count + 1 >= @maxFailures, 0, failed_login_count + 1)
            WHERE user_id = @userId;
            SELECT IF(locked_until_utc = @lockUntilUtc, @maxFailures, failed_login_count) FROM app_user WHERE user_id = @userId;
            """, new { userId, maxFailures, lockUntilUtc }, cancellationToken: ct));
    }

    public async Task RecordLoginAsync(long userId, string method, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE app_user
            SET failed_login_count = 0, locked_until_utc = NULL, last_login_utc = UTC_TIMESTAMP(3), last_login_method = @method
            WHERE user_id = @userId
            """, new { userId, method }, cancellationToken: ct));
    }

    public async Task UnlockAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE app_user SET failed_login_count = 0, locked_until_utc = NULL WHERE user_id = @userId", new { userId }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("DELETE FROM app_user WHERE user_id = @userId", new { userId }, cancellationToken: ct)) > 0;
    }

    public async Task<int> CountActiveAdminsAsync(long? exceptUserId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM app_user u JOIN security_role r ON r.security_role_id = u.security_role_id
            WHERE r.code = 'ADMIN' AND u.is_active = 1 AND (@exceptUserId IS NULL OR u.user_id <> @exceptUserId)
            """, new { exceptUserId }, cancellationToken: ct));
    }
}
