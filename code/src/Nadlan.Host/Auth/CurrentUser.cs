using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Nadlan.Core.Security;

namespace Nadlan.Host.Auth;

public static class AuthSchemes
{
    /// <summary>The session cookie.</summary>
    public const string Cookie = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>Short-lived cookie holding the Google result until we match it to a user.</summary>
    public const string External = "External";

    public const string Google = "Google";

    /// <summary>Bearer API tokens (tools).</summary>
    public const string ApiToken = "ApiToken";

    /// <summary>Picks ApiToken when the request carries "Authorization: Bearer", else the cookie.</summary>
    public const string Default = "Nadlan";
}

internal static class NadlanClaims
{
    public const string SessionVersion = "nadlan:sv";
    public const string LoginMethod = "nadlan:amr";
}

public static class CurrentUserExtensions
{
    private const string ItemKey = "Nadlan.UserAccess";

    /// <summary>The signed-in user for this request (loaded by the cookie validator), or null.</summary>
    public static UserAccess? GetUserAccess(this HttpContext context)
        => context.Items.TryGetValue(ItemKey, out var value) ? value as UserAccess : null;

    internal static void SetUserAccess(this HttpContext context, UserAccess access) => context.Items[ItemKey] = access;

    internal static string? LoginMethod(this ClaimsPrincipal principal) => principal.FindFirstValue(NadlanClaims.LoginMethod);

    /// <summary>Issues the session cookie. The cookie carries only ids; permissions are re-read on every request.</summary>
    public static Task SignInUserAsync(this HttpContext context, AppUser user, string method)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(NadlanClaims.SessionVersion, user.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(NadlanClaims.LoginMethod, method),
        }, AuthSchemes.Cookie);
        return context.SignInAsync(AuthSchemes.Cookie, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    }
}

/// <summary>
/// Runs for every authenticated request: the cookie's user must still exist, be active and match the session version
/// (revoked login, "sign out everywhere" and password changes end the session at once, on every app instance).
/// </summary>
internal static class SessionValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var access = principal is not null
                     && long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                     && int.TryParse(principal.FindFirstValue(NadlanClaims.SessionVersion), out var version)
            ? await context.HttpContext.RequestServices.GetRequiredService<AuthService>().LoadSessionAsync(userId, version, context.HttpContext.RequestAborted)
            : null;

        if (access is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthSchemes.Cookie);
            return;
        }

        context.HttpContext.SetUserAccess(access);
    }
}
