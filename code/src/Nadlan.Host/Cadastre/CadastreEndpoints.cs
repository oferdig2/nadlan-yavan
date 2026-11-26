using Nadlan.Core.Cadastre;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Cadastre;

/// <summary>
/// Owners and sales from the Cadastre's lawyer service (migration 031). Admin only for now (owner, 2026-11-24): checked with
/// IsAdmin, not a permission, and everyone else gets 404 as if none of this existed. Never on presentations, public links or
/// deal portals - only the map's parcel card asks.
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

        // The importer: one KAEK's record, a new snapshot (the older ones stay). 400 on a person's ΑΦΜ (CadastreRules).
        app.MapPost("/api/cadastre/records", async (CadastreRecordInput record, UserAccess me, CadastreService cadastre, CancellationToken ct) =>
        {
            RequireAdmin(me);
            var at = await cadastre.SaveAsync(record, me.UserId, ct);
            return Results.Ok(new { kaek = record.Kaek, fetchedUtc = at });
        });

        // The parcel card: the latest snapshot of the Parcel's KAEK.
        app.MapGet("/api/parcels/{parcelId:long}/cadastre", async (long parcelId, UserAccess me, IParcelStore parcels, CadastreService cadastre,
            CancellationToken ct) =>
        {
            if (!me.IsAdmin) throw new EntityNotFoundException("Parcel", parcelId);
            var parcel = await parcels.GetAsync(parcelId, ct) ?? throw new EntityNotFoundException("Parcel", parcelId);
            var kaek = parcel.RegistryId;
            var view = kaek is { Length: 12 } && kaek.All(char.IsAsciiDigit) ? await cadastre.ViewAsync(kaek, ct) : null;
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
                units = view?.Latest is { } s && s.Status == "Found" ? s.Units : Array.Empty<CadastreUnit>(),
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
