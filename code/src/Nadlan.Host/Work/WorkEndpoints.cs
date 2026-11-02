using System.Globalization;
using Nadlan.Core.Files;
using Nadlan.Core.Geo;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Core.Work;
using Nadlan.Host.Auth;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Work;

/// <summary>
/// The staff side of work requests (work.html): sign-in + MANAGE_WORK (Admin always), checked in <see cref="WorkService"/>.
/// Writes answer with the whole request again, so the page redraws from one source. Parcels outside the user's areas are
/// left out of every list and can't be added. Replies carry no e-mail addresses.
/// </summary>
internal static class WorkEndpoints
{
    private const int MaxSearchParcels = 20;

    public sealed record CreateDto(string? Name, long ContractorContactId);

    public sealed record UpdateDto(string? Name, long ContractorContactId, bool ShowSizeToContractor);

    public sealed record CopyDto(string? Name, long ContractorContactId);

    /// <summary>Password: the typed one, or <c>Generate</c> = 6 random digits.</summary>
    public sealed record PasswordDto(string? Password, bool Generate);

    public sealed record AddParcelDto(long ParcelId, int[]? WorkTypeIds);

    public sealed record ApproveDto(long[]? WorkItemIds);

    public sealed record WorkTypeDto(string? Code, string? Name, string? NameEl, bool IsActive, int SortOrder)
    {
        public WorkType ToType() => new() { Code = Code ?? "", Name = Name ?? "", NameEl = NameEl ?? "", IsActive = IsActive, SortOrder = SortOrder };
    }

    private const int MaxReportRows = 5000;

    /// <summary>The report's query string (statuses repeat: ?statuses=OFFERED&amp;statuses=DONE).</summary>
    public sealed record ReportQuery(string[]? Statuses, long? ContractorId, int? WorkTypeId, long? RequestId, int? AreaId, string? Kaek, string? Ot,
        string? Plot, decimal? MinSqm, decimal? MaxSqm, string? DateOf, DateTime? FromUtc, DateTime? ToUtc, string? GroupBy)
    {
        public WorkReportFilter ToFilter() => new()
        {
            Statuses = Statuses?.Where(s => WorkStatuses.All.Contains(s)).ToList(),
            ContractorContactId = ContractorId,
            WorkTypeId = WorkTypeId,
            WorkRequestId = RequestId,
            GeographicAreaId = AreaId,
            Kaek = Kaek,
            Ot = Ot,
            Plot = Plot,
            MinSqm = MinSqm,
            MaxSqm = MaxSqm,
            DateOf = Enum.TryParse<WorkReportDate>(DateOf, true, out var d) ? d : WorkReportDate.Offered,
            FromUtc = FromUtc?.ToUniversalTime(),
            ToUtc = ToUtc?.ToUniversalTime(),
        };
    }

    public sealed record CorrectDto(string? Reason);

    public sealed record RemarksDto(string? Remarks);

    public static void Map(IEndpointRouteBuilder app)
    {
        // One guard for the whole staff group, so a new endpoint can't forget it (the service checks again).
        var group = app.MapGroup("/api/work").WithWorkConflicts().AddEndpointFilter(async (context, next) =>
            context.HttpContext.GetUserAccess()?.Has(Permissions.ManageWork) == true
                ? await next(context)
                : Results.Json(new { error = "WORK_FORBIDDEN", message = "Your role doesn't include work requests." }, statusCode: StatusCodes.Status403Forbidden));

        // The work types list (piece 4): with how many works use each (a used type can't be deleted, its code stays).
        group.MapGet("/work-types", async (UserAccess me, IWorkStore store, CancellationToken ct) =>
        {
            WorkService.Require(me);
            var uses = await store.WorkTypeUseCountsAsync(ct);
            return Results.Ok((await store.ListWorkTypesAsync(ct)).Select(t => new
            {
                t.WorkTypeId, t.Code, t.Name, t.NameEl, t.IsActive, t.SortOrder, uses = uses.GetValueOrDefault(t.WorkTypeId),
            }));
        });

        group.MapPost("/work-types", async (WorkTypeDto dto, UserAccess me, WorkTypeService types, CancellationToken ct) =>
            Results.Ok(new { workTypeId = await types.CreateAsync(me, dto.ToType(), ct) }));

        group.MapPut("/work-types/{id:int}", async (int id, WorkTypeDto dto, UserAccess me, WorkTypeService types, CancellationToken ct) =>
        {
            await types.UpdateAsync(me, id, dto.ToType(), ct);
            return Results.Ok(new { ok = true });
        });

        group.MapDelete("/work-types/{id:int}", async (int id, UserAccess me, WorkTypeService types, CancellationToken ct) =>
        {
            await types.DeleteAsync(me, id, ct);
            return Results.Ok(new { ok = true });
        });

        // ---- the work report (piece 4) --------------------------------------------------------------------------

        // What the filters offer: the contractors and requests there are, the work types, the areas.
        group.MapGet("/report/options", async (UserAccess me, IWorkStore store, IGeographicAreaStore areas, CancellationToken ct) =>
        {
            var requests = await store.ListRequestsAsync(me.Scope, ct);
            return Results.Ok(new
            {
                contractors = requests.GroupBy(r => r.ContractorContactId).Select(g => new { contactId = g.Key, displayName = g.First().ContractorName })
                    .OrderBy(c => c.displayName),
                requests = requests.Select(r => new { r.WorkRequestId, r.Name, contractor = r.ContractorName }),
                workTypes = (await store.ListWorkTypesAsync(ct)).Select(t => new { t.WorkTypeId, t.Name, t.IsActive }),
                areas = (await areas.ListAsync(ct)).Where(a => me.InAreas(a.GeographicAreaId)).Select(a => new { a.GeographicAreaId, a.Name }),
            });
        });

        // One row per work across requests (only Parcels the user may see), and the € totals in SQL, per group and currency.
        group.MapGet("/report", async ([AsParameters] ReportQuery q, UserAccess me, IWorkStore store, CancellationToken ct) =>
        {
            var filter = q.ToFilter();
            var groupBy = Enum.TryParse<WorkReportGroup>(q.GroupBy, true, out var g) ? g : WorkReportGroup.None;
            var rows = await store.ListReportAsync(filter, me.Scope, MaxReportRows + 1, ct);
            return Results.Ok(new
            {
                totals = await store.ReportTotalsAsync(filter, me.Scope, WorkReportGroup.None, ct),
                groups = groupBy == WorkReportGroup.None ? null : await store.ReportTotalsAsync(filter, me.Scope, groupBy, ct),
                truncated = rows.Count > MaxReportRows,
                rows = rows.Take(MaxReportRows).Select(w => new
                {
                    w.WorkItemId, w.WorkRequestId, w.RequestName, w.ContractorContactId, w.ContractorName, w.ParcelId, w.RegistryId, w.Ot, w.Plot,
                    geographicAreaId = w.GeographicAreaId ?? 0, w.GeographicArea, w.OfficialAreaSqm, w.WorkTypeId, w.WorkTypeName, w.Status, w.Price,
                    w.CurrencyCode, estStartDate = w.EstStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    w.OfferedUtc, w.ApprovedUtc, w.StartedUtc, w.DoneUtc, w.PaidUtc,
                }),
            });
        });

        group.MapGet("/contractors", async (UserAccess me, IWorkStore store, CancellationToken ct) =>
        {
            WorkService.Require(me);
            return Results.Ok(new { roleId = await store.ContractorRoleIdAsync(ct), items = await store.ListContractorsAsync(ct) });
        });

        group.MapGet("/requests", async (UserAccess me, IWorkStore store, CancellationToken ct) =>
        {
            WorkService.Require(me);
            var now = DateTime.UtcNow;
            return Results.Ok((await store.ListRequestsAsync(me.Scope, ct)).Select(r => new
            {
                r.WorkRequestId,
                r.Name,
                contractor = new { contactId = r.ContractorContactId, displayName = r.ContractorName },
                r.Parcels,
                works = new { requested = r.Requested, offered = r.Offered, approved = r.Approved, inWork = r.InWork, done = r.Done, paid = r.Paid },
                link = WorkLinkStates.Of(r.LinkEnabled, r.LinkExpiresUtc, r.LockedUntilUtc, now),
                r.HasPassword,
                r.ApprovedEur,
                r.PaidEur,
                r.OtherCurrency,
                r.CreatedUtc,
                r.LastActivityUtc,
            }));
        });

        group.MapPost("/requests", async (CreateDto dto, UserAccess me, WorkService work, CancellationToken ct) =>
            Results.Ok(new { workRequestId = await work.CreateAsync(me, dto.Name, dto.ContractorContactId, ct) }));

        group.MapGet("/requests/{id:long}", async (long id, UserAccess me, WorkService work, IWorkStore store, CancellationToken ct) =>
            Results.Ok(await DetailAsync(id, me, work, store, ct)));

        group.MapPut("/requests/{id:long}", async (long id, UpdateDto dto, UserAccess me, WorkService work, IWorkStore store, CancellationToken ct) =>
        {
            await work.UpdateAsync(me, id, dto.Name, dto.ContractorContactId, dto.ShowSizeToContractor, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        group.MapPost("/requests/{id:long}/copy", async (long id, CopyDto dto, UserAccess me, WorkService work, CancellationToken ct) =>
            Results.Ok(new { workRequestId = await work.CopyAsync(me, id, dto.ContractorContactId, dto.Name, ct) }));

        group.MapGet("/requests/{id:long}/history", async (long id, UserAccess me, WorkService work, IWorkStore store, CancellationToken ct) =>
        {
            await work.GetAsync(me, id, ct);
            return Results.Ok(await store.ListEventsAsync(id, WorkService.HistoryLimit, ct));
        });

        // ---- the link -------------------------------------------------------------------------------------------

        // The token only: the page builds https://<this site>/work-link.html#<token> (the token after "#" never reaches a server log).
        group.MapGet("/requests/{id:long}/link", async (long id, UserAccess me, WorkService work, CancellationToken ct) =>
            Results.Ok(new { token = await work.LinkTokenAsync(me, id, ct) }));

        group.MapPost("/requests/{id:long}/link/enable", async (long id, UserAccess me, WorkService work, IWorkStore store, CancellationToken ct) =>
        {
            await work.SetLinkEnabledAsync(me, id, true, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        group.MapPost("/requests/{id:long}/link/disable", async (long id, UserAccess me, WorkService work, IWorkStore store, CancellationToken ct) =>
        {
            await work.SetLinkEnabledAsync(me, id, false, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        group.MapPost("/requests/{id:long}/link/extend", async (long id, UserAccess me, WorkService work, IWorkStore store, CancellationToken ct) =>
        {
            await work.ExtendLinkAsync(me, id, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        // The password comes back once, here, to send to the contractor; afterwards only "set / not set".
        group.MapPost("/requests/{id:long}/password", async (long id, PasswordDto dto, UserAccess me, WorkService work, IWorkStore store,
            CancellationToken ct) =>
        {
            if (!dto.Generate && dto.Password is null)
            {
                throw new DomainValidationException("WORK_PASSWORD_LENGTH", "Type a password, or press Generate.");
            }

            var password = await work.SetPasswordAsync(me, id, dto.Generate ? null : dto.Password, ct);
            return Results.Ok(new { password, request = await DetailAsync(id, me, work, store, ct) });
        });

        group.MapDelete("/requests/{id:long}/password", async (long id, UserAccess me, WorkService work, IWorkStore store, CancellationToken ct) =>
        {
            await work.RemovePasswordAsync(me, id, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        // ---- Parcels and works ----------------------------------------------------------------------------------

        // Adding a Parcel: by KAEK (contains, 3+ characters) or by OT + plot (exact), only in the user's areas - as the
        // Asset table's "+". inRequest marks the ones already in this request.
        group.MapGet("/parcels", async (string? kaek, string? ot, string? plot, long? requestId, UserAccess me, IParcelStore parcels,
            IGeographicAreaStore areas, IWorkStore store, CancellationToken ct) =>
        {
            WorkService.Require(me);
            var k = kaek?.Trim();
            var byKaek = !string.IsNullOrEmpty(k);
            if (byKaek && k!.Length < 3)
            {
                throw new DomainValidationException("WORK_SEARCH_SHORT", "Type at least 3 characters of the KAEK.");
            }

            if (!byKaek && (string.IsNullOrWhiteSpace(ot) || string.IsNullOrWhiteSpace(plot)))
            {
                throw new DomainValidationException("WORK_SEARCH_EMPTY", "Type a KAEK, or an OT and a plot number.");
            }

            var found = await parcels.QueryAsync(new ParcelQuery
            {
                RegistryId = byKaek ? k : null,
                Ot = byKaek ? null : ot,
                Plot = byKaek ? null : plot,
                Limit = MaxSearchParcels + 1,
                Scope = me.Scope,
            }, ct);
            return Results.Ok(await SearchReplyAsync(found, found.Count > MaxSearchParcels, requestId, me, areas, store, ct));
        });

        group.MapPost("/requests/{id:long}/parcels", async (long id, AddParcelDto dto, UserAccess me, WorkService work, IWorkStore store,
            CancellationToken ct) =>
        {
            var added = await work.AddParcelWorksAsync(me, id, dto.ParcelId, dto.WorkTypeIds, ct);
            return Results.Ok(new { added = added.Count, request = await DetailAsync(id, me, work, store, ct) });
        });

        group.MapDelete("/requests/{id:long}/parcels/{parcelId:long}", async (long id, long parcelId, UserAccess me, WorkService work,
            IWorkStore store, CancellationToken ct) =>
        {
            await work.RemoveParcelAsync(me, id, parcelId, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        // ---- staff steps (piece 2): approve, paid, correct ------------------------------------------------------

        // One work, or several ("Approve selected": the ones not Offered are skipped).
        group.MapPost("/requests/{id:long}/approve", async (long id, ApproveDto dto, UserAccess me, WorkService work, IWorkStore store,
            CancellationToken ct) =>
        {
            var approved = await work.ApproveAsync(me, id, dto.WorkItemIds, ct);
            return Results.Ok(new { approved, request = await DetailAsync(id, me, work, store, ct) });
        });

        group.MapPost("/requests/{id:long}/works/{workItemId:long}/paid", async (long id, long workItemId, UserAccess me, WorkService work,
            IWorkStore store, CancellationToken ct) =>
        {
            await work.PaidAsync(me, id, workItemId, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        // A remark on one work (staff; the contractor sees it under that work). Empty clears it.
        group.MapPut("/requests/{id:long}/works/{workItemId:long}/remarks", async (long id, long workItemId, RemarksDto dto, UserAccess me,
            WorkService work, IWorkStore store, CancellationToken ct) =>
        {
            await work.SetRemarksAsync(me, id, workItemId, dto.Remarks, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        group.MapPost("/requests/{id:long}/works/{workItemId:long}/correct", async (long id, long workItemId, CorrectDto dto, UserAccess me,
            WorkService work, IWorkStore store, CancellationToken ct) =>
        {
            await work.CorrectAsync(me, id, workItemId, dto.Reason, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        // ---- the request's map (piece 2) ------------------------------------------------------------------------

        group.MapGet("/requests/{id:long}/shapes", async (long id, UserAccess me, WorkService work, IWorkStore store, CancellationToken ct) =>
        {
            await work.GetAsync(me, id, ct);
            return Results.Ok((await store.ListParcelShapesAsync(id, me.Scope, ct)).Select(s => new
            {
                s.ParcelId,
                s.RegistryId,
                s.Status,
                geometry = GeoJson.Polygon(GeoPolygon.FromWkt(s.GeometryWkt)),
            }));
        });

        // "Pick on the map": the Parcels under a point, in the user's areas (then added as from the search).
        group.MapGet("/parcels/at", async (double lat, double lng, long? requestId, UserAccess me, IParcelStore parcels, IGeographicAreaStore areas,
            IWorkStore store, CancellationToken ct) =>
        {
            const double Box = 0.0005; // ~50 m around the point; the polygon test below decides
            var found = (await parcels.QueryAsync(new ParcelQuery
            {
                Area = new GeoBounds(lng - Box, lat - Box, lng + Box, lat + Box),
                Limit = 200,
                Scope = me.Scope,
            }, ct)).Where(p => p.Geometry.Contains(new GeoPoint(lng, lat))).ToList();
            return Results.Ok(await SearchReplyAsync(found, false, requestId, me, areas, store, ct));
        });

        group.MapDelete("/requests/{id:long}/works/{workItemId:long}", async (long id, long workItemId, UserAccess me, WorkService work,
            IWorkStore store, CancellationToken ct) =>
        {
            await work.RemoveWorkAsync(me, id, workItemId, ct);
            return Results.Ok(await DetailAsync(id, me, work, store, ct));
        });

        // ---- a Parcel's conversation and work pictures (piece 3) ------------------------------------------------

        group.MapGet("/requests/{id:long}/parcels/{parcelId:long}/messages", async (long id, long parcelId, UserAccess me, WorkService work,
            CancellationToken ct) => Results.Ok(StaffMessages((await work.GetAsync(me, id, ct)).ContractorName, await work.MessagesAsync(me, id, parcelId, ct))));

        group.MapPost("/requests/{id:long}/parcels/{parcelId:long}/messages", async (long id, long parcelId, MessageDto dto, UserAccess me,
            WorkService work, CancellationToken ct) =>
        {
            await work.SendMessageAsync(me, id, parcelId, dto.Body, ct);
            return Results.Ok(StaffMessages((await work.GetAsync(me, id, ct)).ContractorName, await work.MessagesAsync(me, id, parcelId, ct)));
        });

        // The work pictures of one Parcel, with what was read from each (taken, camera, place) and the phone's position.
        group.MapGet("/requests/{id:long}/parcels/{parcelId:long}/files", async (long id, long parcelId, UserAccess me, AccessPolicy policy,
            WorkService work, IWorkStore store, IFileUrlProvider urls, CancellationToken ct) =>
        {
            await work.GetAsync(me, id, ct);
            await policy.RequireParcelViewAsync(me, parcelId, ct);
            return Results.Ok((await store.ListWorkFilesAsync(id, parcelId, ct)).Select(f => StaffFile(f, urls)));
        });

        group.MapPost("/requests/{id:long}/files/{fileId:long}/copy-to-parcel", async (long id, long fileId, UserAccess me, WorkMediaService media,
            IWorkStore store, IFileUrlProvider urls, CancellationToken ct) =>
        {
            await media.CopyToParcelAsync(me, id, fileId, ct);
            return Results.Ok(StaffFile((await store.GetWorkFileAsync(id, fileId, ct))!, urls));
        });

        // ---- the contractor's location (piece 3) ----------------------------------------------------------------

        // The last position (any day) and one day's trail; the page asks for its own local day (fromUtc .. toUtc).
        group.MapGet("/requests/{id:long}/locations", async (long id, DateTime fromUtc, DateTime toUtc, UserAccess me, WorkService work,
            CancellationToken ct) =>
        {
            var (latest, trail) = await work.LocationsAsync(me, id, fromUtc.ToUniversalTime(), toUtc.ToUniversalTime(), ct);
            return Results.Ok(new
            {
                latest,
                trail = trail.Select(p => new[] { p.Latitude, p.Longitude }),
                truncated = trail.Count >= WorkService.MaxTrailPoints,
            });
        });
    }

    public sealed record MessageDto(string? Body);

    private static object StaffMessages(string contractor, IReadOnlyList<WorkMessage> messages)
        => messages.Select(m => new
        {
            m.WorkMessageId,
            fromContractor = m.AuthorKind == WorkActorKinds.Contractor,
            author = m.AuthorKind == WorkActorKinds.Contractor ? contractor : m.AuthorName ?? "GreekPlot",
            m.Body,
            m.CreatedUtc,
        });

    private static object StaffFile(WorkFile f, IFileUrlProvider urls)
    {
        var video = f.MimeType.StartsWith("video/", StringComparison.Ordinal);
        return new
        {
            f.WorkFileId,
            f.WorkItemId,
            f.WorkTypeName,
            kind = video ? "video" : "image",
            name = f.OriginalName,
            url = urls.GetUrl(f.StorageKey, f.OriginalName),
            downloadUrl = urls.GetDownloadUrl(f.StorageKey, f.OriginalName),
            thumbUrl = f.HasThumbnail ? urls.GetUrl(FileAttachment.ThumbnailKey(f.StorageKey)) : null,
            f.FileSize,
            f.TakenLocal,
            f.CameraMake,
            f.CameraModel,
            f.Latitude,
            f.Longitude,
            f.Width,
            f.Height,
            f.DurationS,
            f.UploadLatitude,
            f.UploadLongitude,
            f.UploadAccuracyM,
            f.UploadedUtc,
            copied = f.CopiedToFileAttachmentId is not null,
        };
    }

    /// <summary>Found Parcels for "+ Add parcel": numbers, area, size and whether this request has them already.</summary>
    private static async Task<object> SearchReplyAsync(IReadOnlyList<Parcel> found, bool more, long? requestId, UserAccess me,
        IGeographicAreaStore areas, IWorkStore store, CancellationToken ct)
    {
        var names = (await areas.ListAsync(ct)).ToDictionary(a => a.GeographicAreaId, a => a.Name);
        var inRequest = requestId is long rid
            ? (await store.ListItemsAsync(rid, me.Scope, ct)).Select(w => w.ParcelId).ToHashSet()
            : new HashSet<long>();
        return new
        {
            more,
            items = found.Take(MaxSearchParcels).Select(p => new
            {
                parcelId = p.ParcelId,
                registryId = p.RegistryId,
                registryIdIsProvisional = p.RegistryIdIsProvisional,
                geographicArea = p.GeographicAreaId is int id && names.TryGetValue(id, out var n) ? n : null,
                ot = p.OT is null ? null : p.OT + p.OTExt,
                plot = p.PlotNumber is null ? null : p.PlotNumber + p.PlotExt,
                officialAreaSqm = p.OfficialAreaSqm,
                inRequest = inRequest.Contains(p.ParcelId),
            }),
        };
    }

    /// <summary>The request page: header, link panel (never the token or the password) and one row per work.</summary>
    private static async Task<object> DetailAsync(long id, UserAccess me, WorkService work, IWorkStore store, CancellationToken ct)
    {
        var r = await work.GetAsync(me, id, ct);
        var items = await store.ListItemsAsync(id, me.Scope, ct);
        var pictures = (await store.ListWorkFilesAsync(id, null, ct)).GroupBy(f => f.WorkItemId).ToDictionary(g => g.Key, g => g.Count());
        var messages = (await store.ListMessagesAsync(id, null, ct)).GroupBy(m => m.ParcelId).ToDictionary(g => g.Key, g => g.Count());
        var now = DateTime.UtcNow;
        return new
        {
            r.WorkRequestId,
            r.Name,
            contractor = new { contactId = r.ContractorContactId, displayName = r.ContractorName },
            r.ShowSizeToContractor,
            link = new
            {
                state = WorkLinkStates.Of(r.LinkEnabled, r.LinkExpiresUtc, r.LockedUntilUtc, now),
                enabled = r.LinkEnabled,
                expiresUtc = r.LinkExpiresUtc,
                hasPassword = r.PasswordHash is not null,
                lockedUntilUtc = r.LockedUntilUtc is DateTime l && l > now ? l : (DateTime?)null,
            },
            copiedFrom = r.CopiedFromRequestId is long src ? new { workRequestId = src, name = r.CopiedFromName } : null,
            contractorLocked = await store.HasProgressAsync(id, ct), // after an offer: another contractor only by "Copy for another contractor"
            r.CreatedUtc,
            createdBy = r.CreatedByName,
            items = items.Select(w => new
            {
                w.WorkItemId,
                w.ParcelId,
                w.RegistryId,
                w.RegistryIdIsProvisional,
                w.Ot,
                w.Plot,
                w.GeographicArea,
                w.OfficialAreaSqm,
                w.ParcelStatus,
                w.WorkTypeId,
                w.WorkTypeName,
                w.Status,
                w.Price,
                w.CurrencyCode,
                estStartDate = w.EstStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                w.OfferedUtc,
                w.ApprovedUtc,
                w.StartedUtc,
                w.DoneUtc,
                w.PaidUtc,
                w.Remarks,
                pictures = pictures.GetValueOrDefault(w.WorkItemId),
                messages = messages.GetValueOrDefault(w.ParcelId), // the Parcel's conversation
            }),
        };
    }
}
