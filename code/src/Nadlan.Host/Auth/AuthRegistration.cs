using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Nadlan.Core.Security;
using Nadlan.Host.Configuration;
using Nadlan.Persistence.MySql;

namespace Nadlan.Host.Auth;

public static class AuthRegistration
{
    /// <summary>Header every state-changing API call must carry (api.js adds it). A cross-site form can't set it.</summary>
    public const string CsrfHeader = "X-Nadlan-Request";

    // Pages that need a signed-in user; the rest (login, password, js, css) are public files.
    private static readonly string[] ProtectedPages = { "/index.html", "/admin.html", "/connect-importer.html", "/present.html" };

    public static IServiceCollection AddNadlanAuth(this IServiceCollection services, IConfiguration configuration, MySqlDatabase db)
    {
        var auth = configuration.GetSection($"{NadlanOptions.SectionName}:Auth").Get<NadlanOptions.AuthOptions>() ?? new();

        services.AddSingleton(new AuthSettings
        {
            MaxFailedLogins = Math.Max(1, auth.MaxFailedLogins),
            Lockout = TimeSpan.FromMinutes(Math.Max(1, auth.LockoutMinutes)),
            ResetLinkLifetime = TimeSpan.FromHours(Math.Max(1, auth.ResetLinkHours)),
            InviteLinkLifetime = TimeSpan.FromHours(Math.Max(1, auth.InviteLinkHours)),
        });
        services.AddSingleton<IEmailSender>(new SmtpEmailSender(auth.Email));

        services.AddDataProtection()
            .SetApplicationName("Nadlan")
            .AddKeyManagementOptions(o => o.XmlRepository = new MySqlXmlRepository(db));

        var authentication = services.AddAuthentication(AuthSchemes.Default)
            .AddPolicyScheme(AuthSchemes.Default, "Cookie or API token", o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? AuthSchemes.ApiToken : AuthSchemes.Cookie)
            .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(AuthSchemes.ApiToken, _ => { })
            .AddCookie(AuthSchemes.Cookie, o =>
            {
                o.Cookie.Name = "nadlan.session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Secure once served over HTTPS
                o.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, auth.SessionHours));
                o.SlidingExpiration = true;
                o.LoginPath = "/login.html";
                o.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = SessionValidator.ValidateAsync,
                    // APIs answer 401/403 JSON; only pages are redirected to the login page.
                    OnRedirectToLogin = ctx => IsApi(ctx.Request)
                        ? WriteErrorAsync(ctx.Response, StatusCodes.Status401Unauthorized, "NOT_SIGNED_IN", "Please sign in.")
                        : RedirectAsync(ctx),
                    OnRedirectToAccessDenied = ctx => WriteErrorAsync(ctx.Response, StatusCodes.Status403Forbidden, "FORBIDDEN", "You don't have permission for this."),
                };
            })
            .AddCookie(AuthSchemes.External, o =>
            {
                o.Cookie.Name = "nadlan.external";
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            });

        if (auth.Google.IsConfigured)
        {
            authentication.AddGoogle(AuthSchemes.Google, o =>
            {
                o.ClientId = auth.Google.ClientId.Trim();
                o.ClientSecret = auth.Google.ClientSecret.Trim();
                o.SignInScheme = AuthSchemes.External;
                o.CallbackPath = "/signin-google"; // register http(s)://<host>/signin-google in Google Cloud
                o.Scope.Add("email");
                o.Scope.Add("profile");
                o.ClaimActions.MapJsonKey("email_verified", "email_verified");
                // Google returns with a top-level GET: Lax is enough and also works on http://localhost.
                o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.Events.OnRemoteFailure = ctx =>
                {
                    ctx.Response.Redirect("/login.html?error=SSO_FAILED");
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        }

        services.AddSingleton(auth.Google);
        services.AddSingleton<ISignInMethods>(new SignInMethods(auth.Google.IsConfigured)); // e.g. which Admins can still sign in

        // Every endpoint needs a signed-in user unless it says AllowAnonymous (only /api/auth/* does).
        services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddHttpContextAccessor();

        // Endpoints take "UserAccess me" as a parameter: the signed-in user of this request.
        services.AddScoped(sp => sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.GetUserAccess()
                                 ?? throw new InvalidOperationException("No signed-in user for this request."));

        if (auth.TrustForwardedHeaders)
        {
            services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                o.KnownNetworks.Clear(); // the load balancer's address isn't known in advance
                o.KnownProxies.Clear();
            });
        }

        return services;
    }

    /// <summary>Order matters: pages are guarded before static files; APIs after authentication.</summary>
    public static WebApplication UseNadlanAuth(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>($"{NadlanOptions.SectionName}:Auth:TrustForwardedHeaders"))
        {
            app.UseForwardedHeaders();
        }

        app.UseDefaultFiles();

        // Signed-out visitors of the map/admin page go to the login page instead of an empty shell.
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method)
                && ProtectedPages.Contains(context.Request.Path.Value ?? "", StringComparer.OrdinalIgnoreCase)
                && !(await context.AuthenticateAsync(AuthSchemes.Cookie)).Succeeded)
            {
                var returnUrl = Uri.EscapeDataString(context.Request.Path + context.Request.QueryString);
                context.Response.Redirect($"/login.html?returnUrl={returnUrl}");
                return;
            }

            await next();
        });

        // Pages, scripts and styles are revalidated on every load (a 304 when unchanged): after a deploy no browser keeps
        // running yesterday's scripts against today's API.
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var ext = Path.GetExtension(ctx.File.Name);
                if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".css", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers.CacheControl = "no-cache";
                }
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();

        app.Use(async (context, next) =>
        {
            if (IsApi(context.Request))
            {
                // Decided by how THIS request authenticated (the handler), never by a claim a cookie could carry.
                var viaToken = context.User.Identity?.AuthenticationType == AuthSchemes.ApiToken;

                // A token is for tools calling the data API. It must not sign in, change a password, reach user admin or mint
                // tokens: a leaked tool token must not turn into a browser session or take over the account.
                if (viaToken && (context.Request.Path.StartsWithSegments("/api/admin") || context.Request.Path.StartsWithSegments("/api/importer")
                    || (context.Request.Path.StartsWithSegments("/api/auth") && !context.Request.Path.StartsWithSegments("/api/auth/me"))))
                {
                    await WriteErrorAsync(context.Response, StatusCodes.Status403Forbidden, "TOKEN_NOT_ALLOWED", "API tokens can't be used for this. Sign in in the browser.");
                    return;
                }

                // CSRF: the session cookie is SameSite=Lax; on top, every write must carry our header.
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                    && !viaToken // bearer tokens carry no cookie: nothing to forge
                    && !context.Request.Headers.ContainsKey(CsrfHeader))
                {
                    await WriteErrorAsync(context.Response, StatusCodes.Status403Forbidden, "CSRF_HEADER_MISSING", "Request rejected (missing request header).");
                    return;
                }

                // An Admin-set password must be replaced before anything else (Appendix 1 §1 MustChangePassword).
                // Not after a Google sign-in: the user didn't use that password.
                if (context.GetUserAccess() is { MustChangePassword: true } && context.User.LoginMethod() == LoginMethods.Password
                    && !context.Request.Path.StartsWithSegments("/api/auth"))
                {
                    await WriteErrorAsync(context.Response, StatusCodes.Status403Forbidden, "PASSWORD_CHANGE_REQUIRED", "Choose a new password first.");
                    return;
                }
            }

            await next();
        });

        return app;
    }

    private static bool IsApi(HttpRequest request) => request.Path.StartsWithSegments("/api");

    private static Task RedirectAsync(RedirectContext<CookieAuthenticationOptions> ctx)
    {
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }

    private static Task WriteErrorAsync(HttpResponse response, int status, string code, string message)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new { error = code, message });
    }
}

/// <summary>Read once at startup, like the Google scheme itself (changing Google settings needs a restart anyway).</summary>
internal sealed record SignInMethods(bool GoogleEnabled) : ISignInMethods;
