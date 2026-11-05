using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Nadlan.Core.Presentation;
using Nadlan.Core.Security;
using Nadlan.Host.Configuration;
using Nadlan.Persistence.MySql.Presentation;

namespace Nadlan.Host.Presentation;

/// <summary>
/// Public presentation links (migration 025, owner 2026-11-05), as the contractor's link: staff make and manage them
/// (Admin and Data person); show.html opens one without signing in. The token travels in the X-Present-Link header (it
/// sits after "#" in the address, so it never reaches a server log). With a password only "state" and "unlock" answer
/// until the phone gave it; the right one leaves an HttpOnly cookie for this link only.
/// </summary>
public static class PresentationLinkEndpoints
{
    public const string TokenHeader = "X-Present-Link";
    private const string CookiePath = "/api/present-link";
    private const string PassPurpose = "Nadlan.Presentation.LinkPass.v1";
    private const string LinkItem = "nadlan.present.link";
    private const string PassItem = "nadlan.present.pass";

    public sealed record CreateDto(long? PortfolioId, long[]? AssetIds, string? Title, string? LastDay, bool GeneratePassword);

    public sealed record ExpiryDto(string? LastDay);

    public sealed record TitleDto(string? Title);

    public sealed record PasswordDto(string? Password, bool Generate);

    public sealed record UnlockDto(string? Password);

    public static IServiceCollection AddPresentationLinks(this IServiceCollection services)
    {
        services.AddSingleton<IPresentationLinkStore, MySqlPresentationLinkStore>();
        services.AddSingleton<IPresentationLinkSealer, DataProtectionPresentationLinkSealer>();
        services.AddSingleton<PresentationLinkService>();
        return services;
    }

    public static void MapPresentationLinkEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- staff: make and manage (SHARE_PRESENTATION) ----------------------------------------------------------

        var staff = app.MapGroup("/api/presentation/links");

        // A Portfolio's links (?portfolioId=12), or those of exactly these Assets (?assetIds=5&assetIds=9).
        staff.MapGet("", async (long? portfolioId, long[]? assetIds, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
            Results.Ok((await links.ListAsync(me, portfolioId, assetIds, ct)).Select(l => View(l, links))));

        staff.MapPost("", async (CreateDto dto, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
        {
            var (link, token) = await links.CreateAsync(me, dto.PortfolioId, dto.AssetIds, PresentationEndpoints.CleanTitle(dto.Title), dto.LastDay, ct);
            var password = dto.GeneratePassword ? await links.SetPasswordAsync(me, link.PresentationLinkId, null, ct) : null;
            return Results.Ok(new { link = View(await links.GetAsync(me, link.PresentationLinkId, ct), links), token, password });
        });

        staff.MapGet("/{id:long}/token", async (long id, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
            Results.Ok(new { token = await links.TokenAsync(me, id, ct) }));

        staff.MapPost("/{id:long}/enable", (long id, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
            Done(links, me, id, links.SetEnabledAsync(me, id, true, ct), ct));

        staff.MapPost("/{id:long}/disable", (long id, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
            Done(links, me, id, links.SetEnabledAsync(me, id, false, ct), ct));

        staff.MapPost("/{id:long}/extend", (long id, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
            Done(links, me, id, links.ExtendAsync(me, id, ct), ct));

        staff.MapPut("/{id:long}/expiry", (long id, ExpiryDto dto, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
            Done(links, me, id, links.SetLastDayAsync(me, id, dto.LastDay, ct), ct));

        staff.MapPut("/{id:long}/title", (long id, TitleDto dto, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
            Done(links, me, id, links.SetTitleAsync(me, id, PresentationEndpoints.CleanTitle(dto.Title), ct), ct));

        // A new address: the old one stops at once.
        staff.MapPost("/{id:long}/renew", async (long id, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
        {
            var token = await links.RenewAsync(me, id, ct);
            return Results.Ok(new { token, link = View(await links.GetAsync(me, id, ct), links) });
        });

        staff.MapPost("/{id:long}/password", async (long id, PasswordDto dto, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
        {
            var password = await links.SetPasswordAsync(me, id, dto.Generate ? null : dto.Password ?? "", ct);
            return Results.Ok(new { password, link = View(await links.GetAsync(me, id, ct), links) });
        });

        staff.MapDelete("/{id:long}/password", (long id, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
            Done(links, me, id, links.RemovePasswordAsync(me, id, ct), ct));

        staff.MapDelete("/{id:long}", async (long id, UserAccess me, PresentationLinkService links, CancellationToken ct) =>
        {
            await links.DeleteAsync(me, id, ct);
            return Results.Ok(new { ok = true });
        });

        // ---- the customer: no sign-in, the link is the access -------------------------------------------------------

        var open = app.MapGroup("/api/present-link").AllowAnonymous().AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            try
            {
                var link = await http.RequestServices.GetRequiredService<PresentationLinkService>()
                    .ResolveAsync(http.Request.Headers[TokenHeader].ToString(), http.RequestAborted);
                http.Items[LinkItem] = link;
                http.Items[PassItem] = ReadPass(http, link);
                return await next(context);
            }
            catch (PresentationLinkException ex)
            {
                return Refused(ex);
            }
        });

        // Before the password: only whether one is needed and whether it is locked - nothing about the properties.
        open.MapGet("/state", (HttpContext http, PresentationLinkService links) =>
        {
            var link = Link(http);
            var locked = link.PasswordHash is not null && links.IsLocked(link);
            return Results.Ok(new { needsPassword = !links.MayEnter(link, Pass(http)), locked, lockedUntilUtc = locked ? link.LockedUntilUtc : null });
        });

        open.MapPost("/unlock", async (UnlockDto dto, HttpContext http, PresentationLinkService links, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var link = Link(http);
            var pass = await links.UnlockAsync(link, dto.Password, ct);
            if (link.PasswordHash is not null)
            {
                http.Response.Cookies.Append(CookieName(link), protection.CreateProtector(PassPurpose).Protect(JsonSerializer.Serialize(pass)),
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = http.Request.IsHttps,
                        SameSite = SameSiteMode.Strict,
                        Path = CookiePath,
                        Expires = pass.ExpiresUtc,
                        IsEssential = true,
                    });
            }

            return Results.Ok(new { ok = true });
        });

        // The presentation: what its creator may present, For sale only; the Maps settings the page needs (no sign-in, so
        // no /api/config/client).
        open.MapGet("/data", async (HttpContext http, PresentationLinkService links, AuthService auth, IOptions<NadlanOptions> options,
            IServiceProvider services, CancellationToken ct) =>
        {
            var link = Link(http);
            links.RequireEntry(link, Pass(http));
            // A creator who left, or lost the right to share, ends the link: it opens nothing, as a disabled one.
            var creator = await auth.LoadActiveUserAsync(link.CreatedByUserId, ct);
            if (creator is null || !creator.Has(Permissions.SharePresentation))
            {
                return Refused(new PresentationLinkException(PresentationLinkRefusal.Inactive));
            }

            List<object> items;
            int total;
            bool truncated;
            try
            {
                (items, total, truncated) = await PresentationEndpoints.BuildAsync(services, creator, link.PortfolioId,
                    link.PortfolioId is null ? link.AssetIdList : null, forSaleOnly: true, ct);
            }
            catch (Nadlan.Core.Validation.EntityNotFoundException)
            {
                (items, total, truncated) = (new List<object>(), 0, false); // the Portfolio is gone or hidden from its creator now
            }

            await links.RecordOpenAsync(link, ct);
            var maps = options.Value.Maps;
            return Results.Ok(new
            {
                title = items.Count > 0 ? link.Title : null,
                items,
                total,
                truncated,
                googleMapsApiKey = maps.GoogleApiKey,
                maps3dChannel = string.IsNullOrWhiteSpace(maps.Maps3dChannel) ? "beta" : maps.Maps3dChannel.Trim(),
            });
        });
    }

    /// <summary>The link as staff see it in the list (never its token: "Copy link" asks for it).</summary>
    private static object View(PresentationLink l, PresentationLinkService links)
    {
        var now = DateTime.UtcNow;
        return new
        {
            id = l.PresentationLinkId,
            l.PortfolioId,
            assetCount = l.PortfolioId is null ? l.AssetIdList.Count : (int?)null,
            l.Title,
            state = !l.LinkEnabled ? "disabled" : l.LinkExpiresUtc <= now ? "expired" : links.IsLocked(l) ? "locked" : "active",
            enabled = l.LinkEnabled,
            expiresUtc = l.LinkExpiresUtc,
            hasPassword = l.PasswordHash is not null,
            lockedUntilUtc = links.IsLocked(l) ? l.LockedUntilUtc : null,
            l.OpenCount,
            l.LastOpenedUtc,
            createdBy = l.CreatedByName,
            l.CreatedUtc,
        };
    }

    private static async Task<IResult> Done(PresentationLinkService links, UserAccess me, long id, Task change, CancellationToken ct)
    {
        await change;
        return Results.Ok(View(await links.GetAsync(me, id, ct), links));
    }

    private static PresentationLink Link(HttpContext http) => (PresentationLink)http.Items[LinkItem]!;

    private static PresentationLinkPass? Pass(HttpContext http) => http.Items[PassItem] as PresentationLinkPass;

    /// <summary>One cookie per link (a buyer may have several): named after the token's hash.</summary>
    private static string CookieName(PresentationLink l) => "nadlan_pl_" + l.LinkTokenHash[..16];

    private static PresentationLinkPass? ReadPass(HttpContext http, PresentationLink link)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName(link), out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(PassPurpose);
            return JsonSerializer.Deserialize<PresentationLinkPass>(protector.Unprotect(value));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static IResult Refused(PresentationLinkException ex) => ex.Refusal switch
    {
        PresentationLinkRefusal.PasswordNeeded => Results.Json(new { error = "PRESENT_LINK_PASSWORD", message = "Type the password for this link." },
            statusCode: StatusCodes.Status401Unauthorized),
        PresentationLinkRefusal.WrongPassword => Results.Json(new { error = "PRESENT_LINK_WRONG_PASSWORD", message = "Wrong password." },
            statusCode: StatusCodes.Status401Unauthorized),
        PresentationLinkRefusal.Locked => Results.Json(new { error = "PRESENT_LINK_LOCKED", message = "Too many wrong passwords. Try again later.",
            lockedUntilUtc = ex.LockedUntilUtc }, statusCode: StatusCodes.Status423Locked),
        _ => Results.Json(new { error = "PRESENT_LINK_INACTIVE", message = "This link isn't active. Ask the person who sent it for a new one." },
            statusCode: StatusCodes.Status404NotFound),
    };
}

/// <summary>The link token sealed with the app's data protection keys (kept in MySQL, so they survive restarts).</summary>
public sealed class DataProtectionPresentationLinkSealer : IPresentationLinkSealer
{
    private readonly IDataProtector _protector;

    public DataProtectionPresentationLinkSealer(IDataProtectionProvider protection)
    {
        _protector = protection.CreateProtector("Nadlan.Presentation.LinkToken.v1");
    }

    public string Seal(string token) => _protector.Protect(token);

    public string Unseal(string sealedToken) => _protector.Unprotect(sealedToken);
}
