using Nadlan.Core.Cadastre;
using Nadlan.Core.Files;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Cadastre;

/// <summary>
/// Owners and sales from the Cadastre's lawyer service (migrations 031, 035). Admin only for now (owner, 2026-11-24): checked
/// with IsAdmin, not a permission, and everyone else gets 404 as if none of this existed. Never on presentations, public links
/// or deal portals - only the map's parcel card asks. V2 (owner, 2026-11-26): the stored extracts and summaries, Admin only,
/// every open logged.
/// </summary>
public static class CadastreEndpoints
{
    public static void MapCadastreEndpoints(this IEndpointRouteBuilder app)
    {
        // The importer: an area's real KAEKs still to research (any status but Error counts as fetched).
        app.MapGet("/api/cadastre/kaeks", async (int areaId, UserAccess me, CadastreService cadastre, CancellationToken ct) =>
        {
            RequireAdmin(me);
            var (kaeks, fetched) = await cadastre.AreaKaeksAsync(areaId, ct);
            var todo = kaeks.Where(k => !fetched.Contains(k)).ToList();
            return Results.Ok(new { areaId, total = kaeks.Count, alreadyFetched = kaeks.Count - todo.Count, kaeks = todo });
        });

        // The importer: one KAEK's record, a new snapshot (the older ones stay). The reply gives each unit's row id, which the
        // importer's document uploads refer to.
        app.MapPost("/api/cadastre/records", async (CadastreRecordInput record, UserAccess me, CadastreService cadastre, CancellationToken ct) =>
        {
            RequireAdmin(me);
            var saved = await cadastre.SaveAsync(record, me.UserId, ct);
            return Results.Ok(new { kaek = record.Kaek, fetchedUtc = saved.FetchedUtc, units = saved.Units });
        });

        // The importer: one PDF it kept (the descriptive extract, the diagram, the last sale's summary), the bytes as the body.
        app.MapPost("/api/cadastre/documents", async (HttpRequest request, long fetchId, string? kind, string? regNo, int? regYear, bool? textReadable,
            UserAccess me, CadastreService cadastre, CancellationToken ct) =>
        {
            RequireAdmin(me);
            if (request.ContentLength is > CadastreService.MaxDocumentBytes)
            {
                throw new DomainValidationException("CADASTRE_BAD_DOCUMENT", "A document is at most 20 MB.");
            }

            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                body.Write(buffer, 0, read);
                if (body.Length > CadastreService.MaxDocumentBytes) throw new DomainValidationException("CADASTRE_BAD_DOCUMENT", "A document is at most 20 MB.");
            }

            var info = await cadastre.AddDocumentAsync(fetchId, kind, regNo, regYear, textReadable == true, body.ToArray(), me.UserId, ct);
            return Results.Ok(info);
        });

        // An Admin opens a stored document: logged (who, what, when), then a short-lived signed link. 404 for anyone else.
        app.MapGet("/api/cadastre/documents/{documentId:long}/content", async (long documentId, UserAccess me, CadastreService cadastre,
            IFileUrlProvider urls, CancellationToken ct) =>
        {
            if (!me.IsAdmin) throw new EntityNotFoundException("CadastreDocument", documentId);
            var doc = await cadastre.OpenDocumentAsync(documentId, me.UserId, me.DisplayName, ct);
            var url = urls.GetUrl(doc.StorageKey, doc.FileName);
            return url.Length == 0
                ? Results.Json(new { error = "STORAGE_NOT_CONFIGURED", message = "File storage is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Redirect(url);
        });

        // The parcel card: the latest snapshot of the Parcel's KAEK.
        app.MapGet("/api/parcels/{parcelId:long}/cadastre", async (long parcelId, UserAccess me, IParcelStore parcels, CadastreService cadastre,
            CancellationToken ct) =>
        {
            if (!me.IsAdmin) throw new EntityNotFoundException("Parcel", parcelId);
            var parcel = await parcels.GetAsync(parcelId, ct) ?? throw new EntityNotFoundException("Parcel", parcelId);
            var kaek = parcel.RegistryId;
            var view = kaek is { Length: 12 } && kaek.All(char.IsAsciiDigit) ? await cadastre.ViewAsync(kaek, ct) : null;
            var units = view?.Latest is { } s && s.Status == "Found" ? s.Units : Array.Empty<CadastreUnit>();
            return Results.Ok(new
            {
                kaek,
                researched = view is not null,
                fetchedUtc = view?.Latest?.FetchedUtc,
                status = view?.Latest?.Status,
                fetchedBy = view?.Latest?.FetchedBy,
                failedAttempt = view?.FailedAttempt,
                lastSale = view?.LastSale,
                seizures = view?.Seizures ?? Array.Empty<CadastreSeizure>(),
                units,
                // "Cadastre says OT 18": a suggestion only (owner, 2026-11-26: never written to the parcel's OT).
                otSuggestion = units.Select(u => u.OtText).FirstOrDefault(t => t is not null),
                otherParcels = view?.OtherParcels ?? new Dictionary<string, IReadOnlyList<OtherParcel>>(),
            });
        });
    }

    private static void RequireAdmin(UserAccess me)
    {
        if (!me.IsAdmin)
        {
            throw new EntityNotFoundException("Page", "cadastre");
        }
    }
}
