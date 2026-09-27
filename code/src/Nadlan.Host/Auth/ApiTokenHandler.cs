using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Nadlan.Core.Security;

namespace Nadlan.Host.Auth;

/// <summary>
/// "Authorization: Bearer nad_..." for tools (the KAEK importer). The token acts as its user with that user's permissions.
/// No cookie is involved, so no CSRF check applies to these requests.
/// </summary>
internal sealed class ApiTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public ApiTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var access = await Context.RequestServices.GetRequiredService<AuthService>().LoadApiTokenAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (access is null)
        {
            return AuthenticateResult.Fail("Invalid, expired or revoked API token.");
        }

        Context.SetUserAccess(access);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, access.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, access.DisplayName),
            new Claim(NadlanClaims.LoginMethod, LoginMethods.ApiToken),
        }, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new { error = "API_TOKEN_INVALID", message = "The API token is invalid, expired or revoked." });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { error = "FORBIDDEN", message = "You don't have permission for this." });
    }
}
