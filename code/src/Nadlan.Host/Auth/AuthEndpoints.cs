using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Configuration;

namespace Nadlan.Host.Auth;

/// <summary>
/// Sign-in API. No registration endpoint on purpose: users exist only when an Admin creates them.
/// Everything here is anonymous except /me, /logout and /change-password.
/// </summary>
public static class AuthEndpoints
{
    public sealed record LoginDto(string? Email, string? Password);

    public sealed record ChangePasswordDto(string? CurrentPassword, string? NewPassword);

    public sealed record ForgotDto(string? Email);

    public sealed record ResetDto(string? Token, string? NewPassword);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        // What the login page may offer.
        group.MapGet("/options", (NadlanOptions.GoogleSsoOptions google, IEmailSender email) =>
            Results.Ok(new { googleEnabled = google.IsConfigured, passwordResetByEmail = email.IsConfigured })).AllowAnonymous();

        group.MapPost("/login", async (LoginDto dto, AuthService auth, HttpContext http, CancellationToken ct) =>
        {
            var result = await auth.PasswordLoginAsync(dto.Email, dto.Password, ct);
            if (!result.Succeeded)
            {
                return Results.Json(new { error = result.Error, message = result.Message }, statusCode: StatusCodes.Status401Unauthorized);
            }

            await http.SignInUserAsync(result.User!, LoginMethods.Password);
            return Results.Ok(new { mustChangePassword = result.User!.MustChangePassword });
        }).AllowAnonymous();

        // Google: the browser navigates here; Google redirects back to /signin-google, then to /api/auth/google/complete.
        group.MapGet("/google", (string? returnUrl, NadlanOptions.GoogleSsoOptions google) =>
        {
            if (!google.IsConfigured)
            {
                return Results.Redirect("/login.html?error=SSO_NOT_CONFIGURED");
            }

            var props = new AuthenticationProperties { RedirectUri = "/api/auth/google/complete?returnUrl=" + Uri.EscapeDataString(SafeReturnUrl(returnUrl)) };
            return Results.Challenge(props, new[] { AuthSchemes.Google });
        }).AllowAnonymous();

        group.MapGet("/google/complete", async (string? returnUrl, AuthService auth, HttpContext http, CancellationToken ct) =>
        {
            var external = await http.AuthenticateAsync(AuthSchemes.External);
            await http.SignOutAsync(AuthSchemes.External);
            if (!external.Succeeded || external.Principal is null)
            {
                return Results.Redirect("/login.html?error=SSO_FAILED");
            }

            var email = external.Principal.FindFirstValue(ClaimTypes.Email);
            var verified = string.Equals(external.Principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
            var result = await auth.ExternalLoginAsync(LoginMethods.Google, email, verified, ct);
            if (!result.Succeeded)
            {
                return Results.Redirect($"/login.html?error={result.Error}&email={Uri.EscapeDataString(email ?? "")}");
            }

            await http.SignInUserAsync(result.User!, LoginMethods.Google);
            return Results.Redirect(SafeReturnUrl(returnUrl));
        }).AllowAnonymous();

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AuthSchemes.Cookie);
            return Results.NoContent();
        }).AllowAnonymous();

        // The signed-in user and what the UI may offer (the server re-checks every call anyway).
        group.MapGet("/me", (UserAccess me, HttpContext http, IEmailSender email) => Results.Ok(new
        {
            me.UserId,
            me.Email,
            me.DisplayName,
            me.ContactId,
            role = me.RoleCode,
            roleName = me.RoleName,
            me.IsAdmin,
            permissions = me.IsAdmin ? new[] { "*" } : me.Permissions.OrderBy(p => p).ToArray(),
            fileCategories = me.VisibleFileCategories,
            mustChangePassword = me.MustChangePassword && http.User.LoginMethod() == LoginMethods.Password,
            me.HasPassword,
            loginMethod = http.User.LoginMethod(),
            canCreateAsset = AccessPolicy.CanCreateAsset(me),
        }));

        group.MapPost("/change-password", async (ChangePasswordDto dto, UserAccess me, AuthService auth, HttpContext http, CancellationToken ct) =>
        {
            var user = await auth.ChangePasswordAsync(me.UserId, dto.CurrentPassword, dto.NewPassword, ct);
            await http.SignInUserAsync(user, http.User.LoginMethod() ?? LoginMethods.Password); // new session version; others are signed out
            return Results.NoContent();
        });

        // Always the same answer, so it can't be used to find out which emails have accounts.
        group.MapPost("/forgot", async (ForgotDto dto, AuthService auth, IEmailSender email, HttpContext http,
            IOptions<NadlanOptions> options, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!email.IsConfigured)
            {
                return Results.Json(new { error = "EMAIL_NOT_CONFIGURED", message = "Password reset by email is not set up. Ask your administrator for a reset link." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var baseUrl = BaseUrl(http, options.Value); // fails (400) when no public address is configured
            var created = await auth.CreateResetTokenAsync(dto.Email, ct);
            if (created is { } c)
            {
                var (token, user) = c;
                var link = $"{baseUrl}/password.html?token={Uri.EscapeDataString(token)}";
                try
                {
                    await email.SendAsync(user.Email, "GreekPlot - reset your password",
                        $"Hello {user.DisplayName},\n\nTo choose a new password, open this link (valid for a short time, once):\n{link}\n\n" +
                        "If you didn't ask for this, ignore this email - your password stays as it is.\n", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logs.CreateLogger("Nadlan.Auth").LogError(ex, "Password reset email to user {UserId} failed", user.UserId);
                }
            }

            return Results.Ok(new { message = "If this email has an account, a reset link is on its way." });
        }).AllowAnonymous();

        group.MapGet("/reset", async (string? token, AuthService auth, CancellationToken ct) =>
        {
            var (found, user) = await auth.ValidateTokenAsync(token, ct);
            return Results.Ok(new { user.Email, user.DisplayName, purpose = found.Purpose });
        }).AllowAnonymous();

        group.MapPost("/reset", async (ResetDto dto, AuthService auth, HttpContext http, CancellationToken ct) =>
        {
            var user = await auth.ResetPasswordAsync(dto.Token, dto.NewPassword, ct);
            await http.SignInUserAsync(user, LoginMethods.Password);
            return Results.NoContent();
        }).AllowAnonymous();
    }

    /// <summary>
    /// Only paths on this site, so a crafted link can't bounce the user to another site after sign-in. Browsers drop tabs and
    /// newlines from URLs ("/\t/evil" becomes "//evil"), so any control character, space or backslash is refused outright,
    /// and what is left must still resolve to this site.
    /// </summary>
    internal static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/' || returnUrl.Length > 1 && returnUrl[1] == '/'
            || returnUrl.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\'))
        {
            return "/";
        }

        // Returned is the normalised path, checked again: "/.//evil.com" becomes "//evil.com", which a browser (or the
        // login page's script) would read as a link to evil.com.
        var probe = new Uri(new Uri("http://nadlan.invalid"), returnUrl);
        var safe = probe.PathAndQuery + probe.Fragment;
        return probe.Host == "nadlan.invalid" && safe.StartsWith('/') && !safe.StartsWith("//", StringComparison.Ordinal) ? safe : "/";
    }

    /// <summary>
    /// Base of links in emails and Admin-made password links. Never taken from the request's Host header (anyone can send
    /// any Host and so make a reset email point to their own site): the configured Nadlan:Auth:PublicBaseUrl, or - for
    /// local development only - the request's address when it is this machine.
    /// </summary>
    internal static string BaseUrl(HttpContext http, NadlanOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Auth.PublicBaseUrl))
        {
            return options.Auth.PublicBaseUrl.Trim().TrimEnd('/');
        }

        var host = http.Request.Host.Host;
        var local = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]" || host == "::1";
        return local && http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip)
            ? $"{http.Request.Scheme}://{http.Request.Host}"
            : throw new DomainValidationException("PUBLIC_URL_NOT_SET",
                "The server's public address is not set, so no link can be made. An Admin sets Nadlan:Auth:PublicBaseUrl (e.g. https://greekplot.example.com).");
    }
}
