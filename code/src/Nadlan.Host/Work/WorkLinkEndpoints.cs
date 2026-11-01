using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Nadlan.Core.Geo;
using Nadlan.Core.Work;
using Nadlan.Host.Configuration;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Work;

/// <summary>
/// The contractor's side (work-link.html): no sign-in - the link is the access. The token travels in the X-Work-Link
/// header (it sits after "#" in the address, so it never reaches a server log). Every call resolves the request from the
/// token first and works only on that request. With a password, only "state" and "unlock" answer until the phone has
/// given it; the right password leaves an HttpOnly cookie for this link only (30 days, signed out by a new password).
/// Replies: never the sale price, owners, people, files or staff names; the size only when the request says so.
/// </summary>
internal static class WorkLinkEndpoints
{
    public const string TokenHeader = "X-Work-Link";
    private const string CookiePath = "/api/work-link";
    private const string PassPurpose = "Nadlan.Work.LinkPass.v1";
    private const string RequestItem = "nadlan.work.request";
    private const string PassItem = "nadlan.work.pass";

    public sealed record UnlockDto(string? Password);

    public sealed record OfferDto(long WorkItemId, decimal? Price, string? EstStartDate);

    public sealed record StepDto(long WorkItemId);

    public static void Map(IEndpointRouteBuilder app)
    {
        // Resolves the request (and the phone's pass) once for every call; refusals become one fixed answer each.
        var group = app.MapGroup("/api/work-link").AllowAnonymous().WithWorkConflicts().AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var links = http.RequestServices.GetRequiredService<WorkLinkService>();
            try
            {
                var r = await links.ResolveAsync(http.Request.Headers[TokenHeader].ToString(), http.RequestAborted);
                http.Items[RequestItem] = r;
                http.Items[PassItem] = ReadPass(http, r);
                return await next(context);
            }
            catch (WorkLinkException ex)
            {
                return Refused(ex);
            }
        });

        // Before the password: only whether one is needed, and whether the link is locked. Nothing about the request.
        group.MapGet("/state", (HttpContext http, WorkLinkService links) =>
        {
            var r = Request(http);
            return Results.Ok(new
            {
                needsPassword = !links.MayEnter(r, Pass(http)),
                locked = r.PasswordHash is not null && links.IsLocked(r),
                lockedUntilUtc = r.PasswordHash is not null && links.IsLocked(r) ? r.LockedUntilUtc : null,
                language = "el",
            });
        });

        group.MapPost("/unlock", async (UnlockDto dto, HttpContext http, WorkLinkService links, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var r = Request(http);
            var pass = await links.UnlockAsync(r, dto.Password, ct);
            if (r.PasswordHash is not null)
            {
                http.Response.Cookies.Append(CookieName(r), protection.CreateProtector(PassPurpose).Protect(JsonSerializer.Serialize(pass)),
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = http.Request.IsHttps, // as the session cookie (SameAsRequest): Secure once served over HTTPS
                        SameSite = SameSiteMode.Strict,
                        Path = CookiePath,
                        Expires = pass.ExpiresUtc,
                        IsEssential = true,
                    });
            }

            return Results.Ok(new { ok = true });
        });

        group.MapGet("/request", async (HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options, CancellationToken ct) =>
        {
            var r = Request(http);
            links.RequireEntry(r, Pass(http));
            return Results.Ok(await ViewAsync(r, store, options.Value, ct));
        });

        group.MapPost("/offer", async (OfferDto dto, HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options,
            CancellationToken ct) =>
        {
            var r = Request(http);
            links.RequireEntry(r, Pass(http));
            await links.OfferAsync(r, dto.WorkItemId, dto.Price, dto.EstStartDate, ct);
            return Results.Ok(await ViewAsync(r, store, options.Value, ct));
        });

        group.MapPost("/start", async (StepDto dto, HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options,
            CancellationToken ct) =>
        {
            var r = Request(http);
            links.RequireEntry(r, Pass(http));
            await links.StartAsync(r, dto.WorkItemId, ct);
            return Results.Ok(await ViewAsync(r, store, options.Value, ct));
        });

        group.MapPost("/done", async (StepDto dto, HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options,
            CancellationToken ct) =>
        {
            var r = Request(http);
            links.RequireEntry(r, Pass(http));
            await links.DoneAsync(r, dto.WorkItemId, ct);
            return Results.Ok(await ViewAsync(r, store, options.Value, ct));
        });
    }

    private static WorkRequest Request(HttpContext http) => (WorkRequest)http.Items[RequestItem]!;

    private static WorkLinkPass? Pass(HttpContext http) => http.Items[PassItem] as WorkLinkPass;

    /// <summary>One cookie per link (a contractor may have several on one phone): named after the token's hash.</summary>
    private static string CookieName(WorkRequest r) => "nadlan_wl_" + r.LinkTokenHash[..16];

    private static WorkLinkPass? ReadPass(HttpContext http, WorkRequest r)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName(r), out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(PassPurpose);
            return JsonSerializer.Deserialize<WorkLinkPass>(protector.Unprotect(value));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null; // tampered with, or sealed with a key that is gone: as if never given
        }
    }

    private static IResult Refused(WorkLinkException ex) => ex.Refusal switch
    {
        WorkLinkRefusal.PasswordNeeded => Results.Json(new { error = "WORK_LINK_PASSWORD", message = "Type the password for this link." },
            statusCode: StatusCodes.Status401Unauthorized),
        WorkLinkRefusal.WrongPassword => Results.Json(new { error = "WORK_LINK_WRONG_PASSWORD", message = "Wrong password." },
            statusCode: StatusCodes.Status401Unauthorized),
        WorkLinkRefusal.Locked => Results.Json(new { error = "WORK_LINK_LOCKED", message = "Too many wrong passwords. Try again later.", lockedUntilUtc = ex.LockedUntilUtc },
            statusCode: StatusCodes.Status423Locked),
        _ => Results.Json(new { error = "WORK_LINK_INACTIVE", message = "This link isn't active. Ask the person who sent it." },
            statusCode: StatusCodes.Status404NotFound),
    };

    /// <summary>What the contractor sees: the request's Parcels (map shape, numbers, area, status) and works. Nothing else.</summary>
    internal static async Task<object> ViewAsync(WorkRequest r, IWorkStore store, NadlanOptions options, CancellationToken ct)
    {
        var items = await store.ListItemsAsync(r.WorkRequestId, null, ct);
        var shapes = (await store.ListParcelShapesAsync(r.WorkRequestId, null, ct)).ToDictionary(s => s.ParcelId);
        var maps = options.Maps;
        return new
        {
            name = r.Name,
            contractor = r.ContractorName,
            showSize = r.ShowSizeToContractor,
            mapsKey = maps.GoogleApiKey,
            defaultCenter = new { lat = maps.DefaultCenterLat, lng = maps.DefaultCenterLon },
            parcels = items.GroupBy(w => w.ParcelId).Select(g =>
            {
                var p = g.First();
                return new
                {
                    parcelId = p.ParcelId,
                    kaek = p.RegistryId,
                    ot = p.Ot,
                    plot = p.Plot,
                    area = p.GeographicArea,
                    sizeSqm = r.ShowSizeToContractor ? p.OfficialAreaSqm : null,
                    status = p.ParcelStatus,
                    geometry = shapes.TryGetValue(p.ParcelId, out var s) ? GeoJson.Polygon(GeoPolygon.FromWkt(s.GeometryWkt)) : null,
                    works = g.Select(w => new
                    {
                        w.WorkItemId,
                        name = w.WorkTypeName,
                        nameEl = w.WorkTypeNameEl,
                        w.Status,
                        w.Price,
                        estStartDate = w.EstStartDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        w.OfferedUtc,
                        w.ApprovedUtc,
                        w.StartedUtc,
                        w.DoneUtc,
                        w.PaidUtc,
                    }),
                };
            }),
        };
    }
}
