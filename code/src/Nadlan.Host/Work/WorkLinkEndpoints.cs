using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Nadlan.Core.Files;
using Nadlan.Core.Geo;
using Nadlan.Core.Validation;
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
            return Results.Ok(await ViewAsync(http, r, ct));
        });

        group.MapPost("/offer", async (OfferDto dto, HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options,
            CancellationToken ct) =>
        {
            var r = Request(http);
            links.RequireEntry(r, Pass(http));
            await links.OfferAsync(r, dto.WorkItemId, dto.Price, dto.EstStartDate, ct);
            return Results.Ok(await ViewAsync(http, r, ct));
        });

        group.MapPost("/start", async (StepDto dto, HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options,
            CancellationToken ct) =>
        {
            var r = Request(http);
            links.RequireEntry(r, Pass(http));
            await links.StartAsync(r, dto.WorkItemId, ct);
            return Results.Ok(await ViewAsync(http, r, ct));
        });

        group.MapPost("/done", async (StepDto dto, HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options,
            CancellationToken ct) =>
        {
            var r = Request(http);
            links.RequireEntry(r, Pass(http));
            await links.DoneAsync(r, dto.WorkItemId, ct);
            return Results.Ok(await ViewAsync(http, r, ct));
        });

        // ---- pictures of a work (piece 3): multipart, the phone sends the parts straight to S3 ------------------

        group.MapPost("/files", async (StartFileDto dto, HttpContext http, WorkMediaService media, CancellationToken ct) =>
        {
            var position = dto.Latitude is double lat && dto.Longitude is double lng ? new UploadPosition(lat, lng, dto.AccuracyM) : null;
            return Results.Ok(await media.StartUploadAsync(Entered(http), dto.WorkItemId, dto.FileName, dto.MimeType, dto.FileSize, position, ct));
        });

        group.MapPost("/files/{id:long}/part-urls", async (long id, PartUrlsDto dto, HttpContext http, WorkMediaService media, CancellationToken ct) =>
            Results.Ok(await media.PartUrlsAsync(Entered(http), id, dto.PartNumbers, ct)));

        group.MapPost("/files/{id:long}/complete", async (long id, CompleteDto dto, HttpContext http, WorkMediaService media, CancellationToken ct) =>
        {
            var f = await media.CompleteAsync(Entered(http), id, dto.Parts, ct);
            return Results.Ok(new { f.WorkFileId });
        });

        group.MapPost("/files/{id:long}/abort", async (long id, HttpContext http, WorkMediaService media, CancellationToken ct) =>
        {
            await media.AbortAsync(Entered(http), id, ct);
            return Results.Ok(new { ok = true });
        });

        // The JPEG preview made on the phone (raw body, at most 300 KB).
        group.MapPost("/files/{id:long}/thumbnail", async (long id, HttpRequest request, HttpContext http, WorkMediaService media, CancellationToken ct) =>
        {
            var r = Entered(http);
            if (request.ContentLength is > FileService.MaxThumbnailBytes)
            {
                throw new DomainValidationException("THUMBNAIL_INVALID", $"The preview must be at most {FileService.MaxThumbnailBytes / 1024} KB.");
            }

            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > FileService.MaxThumbnailBytes)
                {
                    throw new DomainValidationException("THUMBNAIL_INVALID", $"The preview must be at most {FileService.MaxThumbnailBytes / 1024} KB.");
                }

                body.Write(buffer, 0, read);
            }

            await media.SaveThumbnailAsync(r, id, body.ToArray(), ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/files/{id:long}/media-data", async (long id, MediaDataDto dto, HttpContext http, WorkMediaService media, CancellationToken ct) =>
        {
            await media.SaveMediaDataAsync(Entered(http), id, dto.Metadata, ct);
            return Results.Ok(new { ok = true });
        });

        // The Parcels' own photos / videos the contractor may see: Marketing, a type a browser shows (as the presentation).
        group.MapGet("/media", async (HttpContext http, IWorkStore store, IFileUrlProvider urls, CancellationToken ct) =>
        {
            var r = Entered(http);
            var list = await store.ListParcelMediaAsync(r.WorkRequestId, WorkMediaService.CustomerCategory, WorkMediaService.CustomerMediaTypes, ct);
            return Results.Ok(list.Select(m =>
            {
                var kind = m.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "image";
                return new
                {
                    m.ParcelId,
                    kind,
                    url = urls.GetUrl(m.StorageKey, (kind == "video" ? "video" : "photo") + Path.GetExtension(m.StorageKey)),
                    thumbUrl = m.HasThumbnail ? urls.GetUrl(FileAttachment.ThumbnailKey(m.StorageKey)) : null,
                    m.TakenLocal,
                };
            }));
        });

        // ---- the conversation (piece 3): staff appear as "GreekPlot", never by name ------------------------------

        group.MapGet("/messages", async (long parcelId, HttpContext http, IWorkStore store, CancellationToken ct) =>
        {
            var r = Entered(http);
            return Results.Ok(MessagesView(r, await store.ListMessagesAsync(r.WorkRequestId, parcelId, ct)));
        });

        group.MapPost("/messages", async (MessageDto dto, HttpContext http, WorkLinkService links, IWorkStore store, CancellationToken ct) =>
        {
            var r = Entered(http);
            await links.SendMessageAsync(r, dto.ParcelId, dto.Body, ct);
            return Results.Ok(MessagesView(r, await store.ListMessagesAsync(r.WorkRequestId, dto.ParcelId, ct)));
        });

        // ---- location (piece 3): only while the page is open, after the contractor agreed ------------------------

        group.MapPost("/consent", async (ConsentDto dto, HttpContext http, WorkLinkService links, CancellationToken ct) =>
        {
            await links.ConsentAsync(Entered(http), dto.Shared, ct);
            return Results.Ok(new { ok = true });
        });

        // The track while "Work on parcel" is On: a batch about every minute (owner, 2026-11-03).
        group.MapPost("/track", async (TrackDto dto, HttpContext http, WorkLinkService links, CancellationToken ct) =>
        {
            var (stored, kept) = await links.RecordTrackAsync(Entered(http), dto.BatchId, dto.SentAt, dto.Entries, ct);
            return Results.Ok(new { stored, kept });
        });
    }

    public sealed record StartFileDto(long WorkItemId, string? FileName, string? MimeType, long FileSize, double? Latitude, double? Longitude, decimal? AccuracyM);

    public sealed record PartUrlsDto(int[]? PartNumbers);

    public sealed record CompleteDto(UploadedPart[]? Parts);

    public sealed record MediaDataDto(MediaMetadata? Metadata);

    public sealed record MessageDto(long ParcelId, string? Body);

    public sealed record ConsentDto(bool Shared);

    /// <summary>sentAt: the phone's clock when sent (ms since 1970) - tells how far off its clock is.</summary>
    public sealed record TrackDto(string? BatchId, long SentAt, WorkTrackEntry?[]? Entries);

    /// <summary>The conversation as the contractor sees it: "GreekPlot" for staff, the contractor's own name for theirs.</summary>
    internal static object MessagesView(WorkRequest r, IReadOnlyList<WorkMessage> messages) => messages.Select(m => new
    {
        m.WorkMessageId,
        mine = m.AuthorKind == WorkActorKinds.Contractor,
        author = m.AuthorKind == WorkActorKinds.Contractor ? r.ContractorName : "GreekPlot",
        m.Body,
        m.CreatedUtc,
    });

    /// <summary>One work picture: signed links to it and its preview (the uploader's file name stays out of the link).</summary>
    internal static object FileView(WorkFile f, IFileUrlProvider urls)
    {
        var kind = f.MimeType.StartsWith("video/", StringComparison.Ordinal) ? "video" : "image";
        return new
        {
            f.WorkFileId,
            kind,
            url = urls.GetUrl(f.StorageKey, (kind == "video" ? "video" : "photo") + Path.GetExtension(f.StorageKey)),
            thumbUrl = f.HasThumbnail ? urls.GetUrl(FileAttachment.ThumbnailKey(f.StorageKey)) : null,
            f.UploadedUtc,
        };
    }

    private static WorkRequest Request(HttpContext http) => (WorkRequest)http.Items[RequestItem]!;

    /// <summary>The request, after the password if it has one.</summary>
    private static WorkRequest Entered(HttpContext http)
    {
        var r = Request(http);
        http.RequestServices.GetRequiredService<WorkLinkService>().RequireEntry(r, Pass(http));
        return r;
    }

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

    private static Task<object> ViewAsync(HttpContext http, WorkRequest r, CancellationToken ct)
    {
        var services = http.RequestServices;
        return ViewAsync(r, services.GetRequiredService<IWorkStore>(), services.GetRequiredService<IOptions<NadlanOptions>>().Value,
            services.GetRequiredService<IFileUrlProvider>(), services.GetRequiredService<IObjectStorage>().IsConfigured, ct);
    }

    /// <summary>
    /// What the contractor sees: the request's Parcels (map shape, numbers, area, status) and works, with the pictures
    /// uploaded to each work. Nothing else.
    /// </summary>
    internal static async Task<object> ViewAsync(WorkRequest r, IWorkStore store, NadlanOptions options, IFileUrlProvider urls, bool storageOn,
        CancellationToken ct)
    {
        var items = await store.ListItemsAsync(r.WorkRequestId, null, ct);
        var shapes = (await store.ListParcelShapesAsync(r.WorkRequestId, null, ct)).ToDictionary(s => s.ParcelId);
        var files = (await store.ListWorkFilesAsync(r.WorkRequestId, null, ct)).ToLookup(f => f.WorkItemId);
        var maps = options.Maps;
        return new
        {
            name = r.Name,
            contractor = r.ContractorName,
            showSize = r.ShowSizeToContractor,
            mapsKey = maps.GoogleApiKey,
            defaultCenter = new { lat = maps.DefaultCenterLat, lng = maps.DefaultCenterLon },
            uploads = storageOn, // pictures can be added (file storage is set up)
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
                        remarks = w.Remarks, // staff's instruction for this work
                        pictures = WorkMediaService.TakesPictures(w.Status), // pictures can be added now
                        files = files[w.WorkItemId].Select(f => FileView(f, urls)),
                    }),
                };
            }),
        };
    }
}
