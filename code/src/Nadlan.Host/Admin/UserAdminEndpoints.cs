using Microsoft.Extensions.Options;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Auth;
using Nadlan.Host.Configuration;

namespace Nadlan.Host.Admin;

/// <summary>
/// User management (Appendix 1 §9.3, Appendix 2 §26-27): users, passwords/links, sign-out, explicit access grants, roles.
/// Whole group requires MANAGE_USERS (checked again inside UserAdminService).
/// </summary>
public static class UserAdminEndpoints
{
    public sealed record UserDto(string? Email, string? DisplayName, long? ContactId, int SecurityRoleId, bool? IsActive,
        bool? MustChangePassword, string? Password);

    public sealed record SetPasswordDto(string? Password, bool? MustChangePassword);

    public sealed record PasswordLinkDto(bool? SendEmail);

    public sealed record GrantDto(string? ResourceType, long ResourceId, string? PermissionCode, DateTime? ExpiresUtc);

    public sealed record RoleDto(string? Code, string? Name, string? Description, bool? IsActive, string[]? PermissionCodes);

    public sealed record TokenDto(string? Name, DateTime? ExpiresUtc);

    public static void MapUserAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").AddEndpointFilter(async (context, next) =>
        {
            var me = context.HttpContext.GetUserAccess();
            return me is not null && me.Has(Permissions.ManageUsers)
                ? await next(context)
                : Results.Json(new { error = "USERS_FORBIDDEN", message = "Only an administrator can manage users." }, statusCode: StatusCodes.Status403Forbidden);
        });

        group.MapGet("/users", async (string? q, bool? includeInactive, UserAccess me, IUserStore users, UserAdminService service, CancellationToken ct) =>
        {
            var shown = new List<object>();
            foreach (var u in await users.SearchAsync(q, includeInactive ?? true, 500, ct))
            {
                shown.Add(ToDto(await service.ForManagerAsync(me, u, ct)));
            }

            return Results.Ok(shown);
        });

        group.MapGet("/users/{userId:long}", async (long userId, UserAccess me, IUserStore users, UserAdminService service, CancellationToken ct) =>
            await users.GetAsync(userId, ct) is { } u ? Results.Ok(ToDto(await service.ForManagerAsync(me, u, ct))) : throw new EntityNotFoundException("User", userId));

        group.MapPost("/users", async (UserDto dto, UserAccess me, UserAdminService service, CancellationToken ct) =>
            Results.Ok(ToDto(await service.CreateAsync(me, ToInput(dto), dto.Password, ct))));

        group.MapPut("/users/{userId:long}", async (long userId, UserDto dto, UserAccess me, UserAdminService service, CancellationToken ct) =>
            Results.Ok(ToDto(await service.UpdateAsync(me, userId, ToInput(dto), ct))));

        group.MapDelete("/users/{userId:long}", async (long userId, UserAccess me, UserAdminService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(me, userId, ct);
            return Results.NoContent();
        });

        group.MapPost("/users/{userId:long}/password", async (long userId, SetPasswordDto dto, UserAccess me, UserAdminService service, CancellationToken ct) =>
        {
            await service.SetPasswordAsync(me, userId, dto.Password, dto.MustChangePassword ?? true, ct);
            return Results.NoContent();
        });

        group.MapDelete("/users/{userId:long}/password", async (long userId, UserAccess me, UserAdminService service, CancellationToken ct) =>
        {
            await service.RemovePasswordAsync(me, userId, ct);
            return Results.NoContent();
        });

        // A one-time "choose your password" link for the Admin to send; emailed too when SMTP is configured and asked for.
        group.MapPost("/users/{userId:long}/password-link", async (long userId, PasswordLinkDto dto, UserAccess me, UserAdminService service,
            IEmailSender email, IOptions<NadlanOptions> options, HttpContext http, CancellationToken ct) =>
        {
            var baseUrl = AuthEndpoints.BaseUrl(http, options.Value); // before the token, so a missing public address changes nothing
            var (token, user) = await service.CreatePasswordLinkAsync(me, userId, ct);
            var link = $"{baseUrl}/password.html?token={Uri.EscapeDataString(token)}";
            var emailed = false;
            if (dto.SendEmail == true && email.IsConfigured)
            {
                await email.SendAsync(user.Email, "GreekPlot - choose your password",
                    $"Hello {user.DisplayName},\n\n{me.DisplayName} set up your GreekPlot account ({user.Email}).\n" +
                    $"Choose your password here (the link works once, for {options.Value.Auth.InviteLinkHours} hours):\n{link}\n", ct);
                emailed = true;
            }

            return Results.Ok(new { link, emailed, expiresHours = options.Value.Auth.InviteLinkHours });
        });

        group.MapPost("/users/{userId:long}/sign-out", async (long userId, UserAccess me, UserAdminService service, CancellationToken ct) =>
        {
            await service.SignOutEverywhereAsync(me, userId, ct);
            return Results.NoContent();
        });

        group.MapPost("/users/{userId:long}/unlock", async (long userId, UserAccess me, UserAdminService service, CancellationToken ct) =>
        {
            await service.UnlockAsync(me, userId, ct);
            return Results.NoContent();
        });

        // API tokens (Bearer) for tools such as the KAEK importer. The raw token is shown once, at creation.
        group.MapGet("/users/{userId:long}/tokens", async (long userId, UserAccess me, UserAdminService service, CancellationToken ct) =>
            Results.Ok(await service.ListApiTokensAsync(me, userId, ct)));

        group.MapPost("/users/{userId:long}/tokens", async (long userId, TokenDto dto, UserAccess me, UserAdminService service, CancellationToken ct) =>
            Results.Ok(new { token = await service.CreateApiTokenAsync(me, userId, dto.Name, dto.ExpiresUtc, ct) }));

        group.MapDelete("/users/{userId:long}/tokens/{apiTokenId:long}", async (long userId, long apiTokenId, UserAccess me,
            UserAdminService service, CancellationToken ct) =>
        {
            await service.RevokeApiTokenAsync(me, userId, apiTokenId, ct);
            return Results.NoContent();
        });

        // Explicit access (ResourceAccessEditor).
        group.MapGet("/users/{userId:long}/access", async (long userId, UserAccess me, UserAdminService service, CancellationToken ct) =>
            Results.Ok(await service.ListGrantsAsync(me, userId, ct)));

        group.MapPost("/users/{userId:long}/access", async (long userId, GrantDto dto, UserAccess me, UserAdminService service, CancellationToken ct) =>
            Results.Ok(new { resourceAccessId = await service.GrantAsync(me, userId, dto.ResourceType, dto.ResourceId, dto.PermissionCode, dto.ExpiresUtc, ct) }));

        group.MapDelete("/users/{userId:long}/access/{resourceAccessId:long}", async (long userId, long resourceAccessId, UserAccess me,
            UserAdminService service, CancellationToken ct) =>
        {
            await service.RevokeAsync(me, userId, resourceAccessId, ct);
            return Results.NoContent();
        });

        // Type-ahead for the grant editor: Admins search everything, user managers only what they can see.
        group.MapGet("/resources", async (string? type, string? q, UserAccess me, UserAdminService service, CancellationToken ct) =>
        {
            var t = ResourceTypes.Normalize(type) ?? throw new DomainValidationException("GRANT_TYPE_INVALID", "Choose Asset, Parcel, Portfolio or Contact.");
            return Results.Ok(await service.SearchResourcesAsync(me, t, q, ct)); // a user manager: only what they can see
        });

        // Roles and the permission catalog.
        group.MapGet("/permissions", async (IRoleStore roles, CancellationToken ct) => Results.Ok(await roles.ListPermissionsAsync(ct)));

        group.MapGet("/roles", async (IRoleStore roles, CancellationToken ct) => Results.Ok(await roles.ListRolesAsync(ct)));

        group.MapPost("/roles", async (RoleDto dto, UserAccess me, UserAdminService service, CancellationToken ct) =>
            Results.Ok(new { securityRoleId = await service.CreateRoleAsync(me, dto.Code, dto.Name, dto.Description, ct) }));

        group.MapPut("/roles/{roleId:int}", async (int roleId, RoleDto dto, UserAccess me, UserAdminService service, CancellationToken ct) =>
        {
            await service.UpdateRoleAsync(me, roleId, dto.Name, dto.Description, dto.IsActive ?? true, dto.PermissionCodes ?? Array.Empty<string>(), ct);
            return Results.NoContent();
        });
    }

    private static UserInput ToInput(UserDto dto)
        => new(dto.Email, dto.DisplayName, dto.ContactId, dto.SecurityRoleId, dto.IsActive ?? true, dto.MustChangePassword ?? true);

    // Never the password hash.
    private static object ToDto(AppUser u) => new
    {
        u.UserId,
        u.Email,
        u.DisplayName,
        u.ContactId,
        u.ContactName,
        u.SecurityRoleId,
        u.RoleCode,
        u.RoleName,
        u.HasPassword,
        u.MustChangePassword,
        u.IsActive,
        isLocked = u.LockedUntilUtc is DateTime l && l > DateTime.UtcNow,
        u.LockedUntilUtc,
        u.LastLoginUtc,
        u.LastLoginMethod,
        u.PasswordChangedUtc,
        u.CreatedUtc,
    };
}
