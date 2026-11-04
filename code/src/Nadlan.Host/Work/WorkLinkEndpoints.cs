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
/// header (it sits after "#" in the address, so it never reaches a server log). Every call resolves the token first: the
/// contractor's link (all his open requests, migration 024) or a request's own link (sent before) - and works only on
/// the requests it opens, each step on the request its work / parcel / picture belongs to. With a password, only "state" and "unlock" answer until the phone has
/// given it; the right password leaves an HttpOnly cookie for this link only (30 days, signed out by a new password).
/// Replies: never the sale price, owners, people, files or staff names; the size only when the request says so.
/// </summary>
internal static class WorkLinkEndpoints
{
    public const string TokenHeader = "X-Work-Link";
    private const string CookiePath = "/api/work-link";
    private const string PassPurpose = "Nadlan.Work.LinkPass.v1";
    private const string ScopeItem = "nadlan.work.scope";
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
                var s = await links.ResolveScopeAsync(http.Request.Headers[TokenHeader].ToString(), http.RequestAborted);
                http.Items[ScopeItem] = s;
                http.Items[PassItem] = ReadPass(http, s);
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
            var s = Scope(http);
            return Results.Ok(new
            {
                needsPassword = !links.MayEnter(s, Pass(http)),
                locked = s.PasswordHash is not null && links.IsLocked(s),
                lockedUntilUtc = s.PasswordHash is not null && links.IsLocked(s) ? s.LockedUntilUtc : null,
                language = "el",
            });
        });

        group.MapPost("/unlock", async (UnlockDto dto, HttpContext http, WorkLinkService links, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var s = Scope(http);
            var pass = await links.UnlockAsync(s, dto.Password, ct);
            if (s.PasswordHash is not null)
            {
                http.Response.Cookies.Append(CookieName(s), protection.CreateProtector(PassPurpose).Protect(JsonSerializer.Serialize(pass)),
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
            return Results.Ok(await ViewAsync(http, Entered(http), ct));
        });

        group.MapPost("/offer", async (OfferDto dto, HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options,
            CancellationToken ct) =>
        {
            var s = Entered(http);
            await links.OfferAsync(await links.RequestOfWorkAsync(s, dto.WorkItemId, ct), dto.WorkItemId, dto.Price, dto.EstStartDate, ct);
            return Results.Ok(await ViewAsync(http, s, ct));
        });

        group.MapPost("/start", async (StepDto dto, HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options,
            CancellationToken ct) =>
        {
            var s = Entered(http);
            await links.StartAsync(await links.RequestOfWorkAsync(s, dto.WorkItemId, ct), dto.WorkItemId, ct);
            return Results.Ok(await ViewAsync(http, s, ct));
        });

        group.MapPost("/done", async (StepDto dto, HttpContext http, WorkLinkService links, IWorkStore store, IOptions<NadlanOptions> options,
            CancellationToken ct) =>
        {
            var s = Entered(http);
            await links.DoneAsync(await links.RequestOfWorkAsync(s, dto.WorkItemId, ct), dto.WorkItemId, ct);
            return Results.Ok(await ViewAsync(http, s, ct));
        });

        // ---- pictures of a work (piece 3): multipart, the phone sends the parts straight to S3 ------------------

        group.MapPost("/files", async (StartFileDto dto, HttpContext http, WorkLinkService links, WorkMediaService media, CancellationToken ct) =>
        {
            var position = dto.Latitude is double lat && dto.Longitude is double lng ? new UploadPosition(lat, lng, dto.AccuracyM) : null;
            return Results.Ok(await media.StartUploadAsync(await links.RequestOfWorkAsync(Entered(http), dto.WorkItemId, ct), dto.WorkItemId, dto.FileName, dto.MimeType, dto.FileSize, position, ct));
        });

        group.MapPost("/files/{id:long}/part-urls", async (long id, PartUrlsDto dto, HttpContext http, WorkLinkService links, WorkMediaService media, CancellationToken ct) =>
            Results.Ok(await media.PartUrlsAsync(await FileRequestAsync(http, links, id, ct), id, dto.PartNumbers, ct)));

        group.MapPost("/files/{id:long}/complete", async (long id, CompleteDto dto, HttpContext http, WorkLinkService links, WorkMediaService media, CancellationToken ct) =>
        {
            var f = await media.CompleteAsync(await FileRequestAsync(http, links, id, ct), id, dto.Parts, ct);
            return Results.Ok(new { f.WorkFileId });
        });

        group.MapPost("/files/{id:long}/abort", async (long id, HttpContext http, WorkLinkService links, WorkMediaService media, CancellationToken ct) =>
        {
            await media.AbortAsync(await FileRequestAsync(http, links, id, ct), id, ct);
            return Results.Ok(new { ok = true });
        });

        // The JPEG preview made on the phone (raw body, at most 300 KB).
        group.MapPost("/files/{id:long}/thumbnail", async (long id, HttpRequest request, HttpContext http, WorkLinkService links, WorkMediaService media, CancellationToken ct) =>
        {
            var r = await FileRequestAsync(http, links, id, ct);
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

        group.MapPost("/files/{id:long}/media-data", async (long id, MediaDataDto dto, HttpContext http, WorkLinkService links, WorkMediaService media, CancellationToken ct) =>
        {
            await media.SaveMediaDataAsync(await FileRequestAsync(http, links, id, ct), id, dto.Metadata, ct);
            return Results.Ok(new { ok = true });
        });

        // The Parcels' own photos / videos the contractor may see: Marketing, a type a browser shows (as the presentation).
        group.MapGet("/media", async (HttpContext http, IWorkStore store, IFileUrlProvider urls, CancellationToken ct) =>
        {
            var list = new List<WorkParcelMedia>();
            foreach (var r in Entered(http).Requests)
            {
                list.AddRange(await store.ListParcelMediaAsync(r.WorkRequestId, WorkMediaService.CustomerCategory, WorkMediaService.CustomerMediaTypes, ct));
            }

            return Results.Ok(list.DistinctBy(m => m.FileAttachmentId).Select(m =>
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

        group.MapGet("/messages", async (long parcelId, HttpContext http, WorkLinkService links, IWorkStore store, CancellationToken ct) =>
        {
            var r = await links.RequestOfParcelAsync(Entered(http), parcelId, ct);
            return Results.Ok(MessagesView(r, await store.ListMessagesAsync(r.WorkRequestId, parcelId, ct)));
        });

        group.MapPost("/messages", async (MessageDto dto, HttpContext http, WorkLinkService links, IWorkStore store, CancellationToken ct) =>
        {
            var r = await links.RequestOfParcelAsync(Entered(http), dto.ParcelId, ct);
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

    private static WorkLinkScope Scope(HttpContext http) => (WorkLinkScope)http.Items[ScopeItem]!;

    /// <summary>What the link opens, after the password if it has one.</summary>
    private static WorkLinkScope Entered(HttpContext http)
    {
        var s = Scope(http);
        http.RequestServices.GetRequiredService<WorkLinkService>().RequireEntry(s, Pass(http));
        return s;
    }

    private static Task<WorkRequest> FileRequestAsync(HttpContext http, WorkLinkService links, long workFileId, CancellationToken ct)
        => links.RequestOfFileAsync(Entered(http), workFileId, ct);

    private static WorkLinkPass? Pass(HttpContext http) => http.Items[PassItem] as WorkLinkPass;

    /// <summary>One cookie per link (a contractor may have several on one phone): named after the token's hash.</summary>
    private static string CookieName(WorkLinkScope s) => "nadlan_wl_" + s.TokenHash[..16];

    private static WorkLinkPass? ReadPass(HttpContext http, WorkLinkScope s)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName(s), out var value) || string.IsNullOrEmpty(value))
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

    private static Task<object> ViewAsync(HttpContext http, WorkLinkScope s, CancellationToken ct)
    {
        var services = http.RequestServices;
        return ViewAsync(s, services.GetRequiredService<IWorkStore>(), services.GetRequiredService<IOptions<NadlanOptions>>().Value,
            services.GetRequiredService<IFileUrlProvider>(), services.GetRequiredService<IObjectStorage>().IsConfigured, ct);
    }

    // A parcel in two of his requests shows the status that matters most to him now.
    private static int ParcelStatusRank(string status) => status switch
    {
        ParcelWorkStatuses.InWork => 0,
        ParcelWorkStatuses.Approved => 1,
        ParcelWorkStatuses.OpenOffers => 2,
        ParcelWorkStatuses.Done => 3,
        _ => 4,
    };

    /// <summary>One request's view (its own link).</summary>
    internal static Task<object> ViewAsync(WorkRequest r, IWorkStore store, NadlanOptions options, IFileUrlProvider urls, bool storageOn, CancellationToken ct)
        => ViewAsync(new WorkLinkScope(null, r, new[] { r }), store, options, urls, storageOn, ct);

    /// <summary>
    /// What the contractor sees: the Parcels of the requests the link opens (map shape, numbers, area, status) and their
    /// works, with the pictures uploaded to each work. Nothing else. A parcel in two of his requests is one card with the
    /// works of both (each work says which request); its status is the newest request's.
    /// </summary>
    internal static async Task<object> ViewAsync(WorkLinkScope s, IWorkStore store, NadlanOptions options, IFileUrlProvider urls, bool storageOn,
        CancellationToken ct)
    {
        var rows = new List<(WorkRequest Request, WorkItemRow Work)>();
        var shapes = new Dictionary<long, WorkParcelShape>();
        var files = new List<WorkFile>();
        foreach (var r in s.Requests)
        {
            rows.AddRange((await store.ListItemsAsync(r.WorkRequestId, null, ct)).Select(w => (r, w)));
            foreach (var shape in await store.ListParcelShapesAsync(r.WorkRequestId, null, ct))
            {
                shapes.TryAdd(shape.ParcelId, shape);
            }

            files.AddRange(await store.ListWorkFilesAsync(r.WorkRequestId, null, ct));
        }

        var filesOf = files.ToLookup(f => f.WorkItemId);
        var maps = options.Maps;
        return new
        {
            name = s.Contractor is null ? s.Request!.Name : s.ContractorName,
            contractor = s.ContractorName,
            requests = s.Requests.Select(r => new { r.WorkRequestId, r.Name }),
            showSize = s.Requests.Any(r => r.ShowSizeToContractor),
            mapsKey = maps.GoogleApiKey,
            defaultCenter = new { lat = maps.DefaultCenterLat, lng = maps.DefaultCenterLon },
            uploads = storageOn, // pictures can be added (file storage is set up)
            parcels = rows.GroupBy(x => x.Work.ParcelId).Select(g =>
            {
                var p = g.First().Work;
                return new
                {
                    parcelId = p.ParcelId,
                    kaek = p.RegistryId,
                    ot = p.Ot,
                    plot = p.Plot,
                    area = p.GeographicArea,
                    sizeSqm = g.Any(x => x.Request.ShowSizeToContractor) ? p.OfficialAreaSqm : null,
                    status = g.Select(x => x.Work.ParcelStatus).MinBy(ParcelStatusRank), // in two requests: the most active
                    requests = g.Select(x => x.Request.Name).Distinct(),
                    geometry = shapes.TryGetValue(p.ParcelId, out var shape) ? GeoJson.Polygon(GeoPolygon.FromWkt(shape.GeometryWkt)) : null,
                    works = g.Select(x => x.Work).Select(w => new
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
                        files = filesOf[w.WorkItemId].Select(f => FileView(f, urls)),
                    }),
                };
            }),
        };
    }
}
